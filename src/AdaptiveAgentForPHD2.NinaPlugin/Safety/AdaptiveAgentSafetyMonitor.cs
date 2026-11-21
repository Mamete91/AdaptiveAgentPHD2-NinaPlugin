#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using AdaptiveAgentForPHD2.NinaPlugin.Telemetry;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>
    /// Safety Monitor virtuale (v1.2). NINA lo consuma come driver ISafetyMonitor sotto la categoria
    /// "N.I.N.A." nella tendina Equipment -> Safety Monitor. Riflette lo stato della guida dell'Adaptive
    /// Agent for PHD2: dichiara unsafe quando STAR_LOST persiste oltre il timeout configurato.
    ///
    /// Non prende decisioni operative: aggiorna solo IsSafe/Connected. NINA decide cosa farne
    /// (pausa/parking/alert) secondo la policy safety configurata dall'utente. Nessun ISequenceMediator.
    ///
    /// NINA legge IsSafe/Connected tramite un DeviceUpdateTimer (polling a DevicePollingInterval),
    /// non via PropertyChanged: impostare Connected=false e' quindi sufficiente per propagare a NINA
    /// l'auto-disconnessione quando l'Agente diventa irraggiungibile (verificato nel pre-flight).
    /// </summary>
    public sealed class AdaptiveAgentSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private readonly PluginSettings _settings;
        private readonly AgentHealthChecker _health;
        private readonly SafetyDecisionEngine _engine;
        // §72 — protezione meridiano: macchina a stati PURA; qui vive solo il
        // coordinamento (lettura montatura/profilo, azioni, toast). `_internalSafe`
        // e' lo stato ONESTO dei latch; IsSafe riportato = _internalSafe || finestra.
        private readonly MeridianProtectionEngine _meridian = new();
        // §73 — riflesso dello stato verso la dashboard dell'Agente (sola presentazione).
        private readonly SafetyStatePublisher _publisher;
        private bool _internalSafe = true;

        private bool _isSafe = true;   // ottimistico all'avvio: safe finche' non si dimostra il contrario
        private bool _connected;
        private bool _subscribed;

        // §72-bis — il flip di NINA in corso e quanti ne ha conclusi (dagli avvisi del
        // mediator della montatura; scritti dal thread del flip, letti dal tick N6).
        private volatile bool _ninaFlipInProgress;
        private int _ninaFlipsCompleted;
        private NINA.Equipment.Interfaces.Mediator.ITelescopeMediator? _flipSource;

        public AdaptiveAgentSafetyMonitor(PluginSettings settings, AgentHealthChecker health, SafetyDecisionEngine engine)
        {
            _settings = settings;
            _health = health;
            _engine = engine;
            _publisher = new SafetyStatePublisher(settings);
        }

        // --- Identita' del driver (vedi pre-flight: stesse convenzioni del SafetyMonitorSimulator di NINA) ---
        // §78 — il nome descrive cio' che il dispositivo MISURA (le condizioni del
        // cielo), non una delle sue conseguenze (il gate di sicurezza). NINA salva il
        // device per Id (stabile, hard-coded sotto): il rename non rompe i profili.
        public string Name => Loc.T("Monitor_Name");
        public string DisplayName => Name;
        // §58-ter — descrizione = manuale d'uso in miniatura: COSA fa + COME si monta la
        // sequenza per il recovery automatico (§57-bis). E' una string del contratto
        // ISafetyMonitor: NINA la renderizza come testo (TextBlock) — niente immagini,
        // ma \n e caratteri unicode di albero sono supportati.
        public string Description => Loc.T("Monitor_Description");
        public string DriverInfo => "Adaptive Agent for PHD2 v1.15.0.0 — sky conditions monitor (virtual safety device)";
        public string DriverVersion => "1.15.1.0";
        public string Category => "N.I.N.A.";
        // GUID stabile, distinto dal GUID del plugin (6F2E9C19-...). Generato una volta sola e hard-coded.
        public string Id => "10A715AD-903C-499E-9CC7-CA8E66A49B7C";

        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();

        public bool IsSafe
        {
            get => _isSafe;
            private set { _isSafe = value; RaisePropertyChanged(); }
        }

        public bool Connected
        {
            get => _connected;
            private set { _connected = value; RaisePropertyChanged(); }
        }

        public async Task<bool> Connect(CancellationToken token)
        {
            _engine.Reset();
            // §126-bis — il monitor e' un singleton che sopravvive allo scollegamento:
            // senza questo, dopo un UNSAFE il monitor ricollegato restava UNSAFE per
            // sempre a motore pulito (verifica del 05/10; ipotesi della forense del
            // 3-4/10, ora provata). Anche la finestra meridiano riparte pulita.
            _internalSafe = true;
            _meridian.Reset();

            // Connected richiede che l'Agente risponda sia a /about sia a /status entro il timeout (3s).
            var health = await _health.CheckOnceAsync().ConfigureAwait(false);
            var snap = await _health.CheckStatusOnceAsync().ConfigureAwait(false);
            if (!health.IsOnline || !snap.IsValid)
            {
                // Fallimento: niente subscribe, niente status polling. NINA mostrera' il driver disconnesso.
                Connected = false;
                return false;
            }

            IsSafe = true; // stato iniziale neutro
            Subscribe();
            _health.StatusPollingEnabled = true;
            Connected = true;
            Logger.Info("Adaptive Agent Safety Monitor: connected — Agent online");
            return true;
        }

        public void Disconnect()
        {
            // Disconnessione ESPLICITA (utente/NINA). Fix N6 (Bug C): NON imposta piu'
            // IsSafe=true — un disconnect non deve mai fabbricare un "sicuro" (NINA ignora
            // IsSafe da disconnesso; Connect() riparte da stato neutro). Inoltre il monitor
            // non si auto-disconnette piu' quando l'agente e' irraggiungibile.
            Unsubscribe();
            _health.StatusPollingEnabled = false;
            _engine.Reset();
            _meridian.Reset();
            _internalSafe = true;   // §126-bis — nessuno stato vecchio alla dashboard
            Connected = false;
            // §73 — la dashboard deve saperlo: monitor scollegato = stato SCONOSCIUTO,
            // mai un verde residuo lasciato sullo schermo (stessa disciplina §55).
            _publisher.Publish("SAFE", null, null, connected: false, internalSafe: _internalSafe,
                               _settings.HealthCheckIntervalSeconds, JudgeName());
        }

        public void SetupDialog() { /* no-op: HasSetupDialog == false */ }

        // --- Equipment generico non usato da questo sensore virtuale (come nel simulatore NINA) ---
        public string Action(string actionName, string actionParameters) => throw new NotImplementedException();
        public string SendCommandString(string command, bool raw = true) => throw new NotImplementedException();
        public bool SendCommandBool(string command, bool raw = true) => throw new NotImplementedException();
        public void SendCommandBlind(string command, bool raw = true) => throw new NotImplementedException();

        /// <summary>
        /// Fix N6 (Bug C-bis): le toast di NINA toccano oggetti WPF con affinita' di thread;
        /// dal thread del timer vanno marshallate sul dispatcher UI. Un'eccezione qui non deve
        /// MAI propagare (interromperebbe la valutazione safety del tick): log Debug e avanti.
        /// </summary>
        private static void ShowToast(Action toast)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(toast);
                }
                else
                {
                    toast();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Adaptive Agent Safety Monitor: toast failed ({ex.Message}) — ignored");
            }
        }

        private void Subscribe()
        {
            if (_subscribed) { return; }
            _subscribed = true;
            _health.StatusChanged += OnHealthChanged;
            _health.StatusUpdated += OnStatusUpdated;
            _ninaFlipInProgress = false;
            try
            {
                _flipSource = AgentServices.Instance.TelescopeMediator;
                if (_flipSource != null)
                {
                    _flipSource.BeforeMeridianFlip += OnBeforeMeridianFlip;
                    _flipSource.AfterMeridianFlip += OnAfterMeridianFlip;
                }
            }
            catch (Exception ex)
            {
                // Fail-inert: senza avvisi vale la regola del cambio di pilastro.
                Logger.Warning($"Meridian protection (§72-bis): flip notifications unavailable ({ex.Message})");
                _flipSource = null;
            }
        }

        private void Unsubscribe()
        {
            if (!_subscribed) { return; }
            _subscribed = false;
            _health.StatusChanged -= OnHealthChanged;
            _health.StatusUpdated -= OnStatusUpdated;
            if (_flipSource != null)
            {
                _flipSource.BeforeMeridianFlip -= OnBeforeMeridianFlip;
                _flipSource.AfterMeridianFlip -= OnAfterMeridianFlip;
                _flipSource = null;
            }
            _ninaFlipInProgress = false;
        }

        /// <summary>§72-bis — NINA inizia il flip (prima di fermare la guida).</summary>
        private Task OnBeforeMeridianFlip(object sender, NINA.Equipment.Interfaces.Mediator.BeforeMeridianFlipEventArgs e)
        {
            _ninaFlipInProgress = true;
            Logger.Info("Meridian protection (§72-bis): NINA meridian flip started");
            return Task.CompletedTask;
        }

        /// <summary>§72-bis — NINA ha concluso il flip (anche annullato): ricentraggio,
        /// ripresa della guida e assestamento sono alle spalle.</summary>
        private Task OnAfterMeridianFlip(object sender, NINA.Equipment.Interfaces.Mediator.AfterMeridianFlipEventArgs e)
        {
            _ninaFlipInProgress = false;
            Interlocked.Increment(ref _ninaFlipsCompleted);
            Logger.Info($"Meridian protection (§72-bis): NINA meridian flip finished (success={e.Success})");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Fix N6 (Bug C): l'irraggiungibilita' dell'Agente NON disconnette piu' il monitor
        /// (prima: Disconnect() + IsSafe=true => la perdita dell'agente diventava "sicuro",
        /// fail-dangerous — provato dai log NINA del 2026-07-10). Il monitor resta connesso;
        /// il decision engine riceve AgentReachable=false a ogni tick e, a sessione attiva,
        /// escala verso UNSAFE con isteresi (AgentLostUnsafePolls).
        /// </summary>
        private void OnHealthChanged(AgentHealth health)
        {
            if (!Connected) { return; }
            if (!health.IsOnline)
            {
                Logger.Info("Adaptive Agent Safety Monitor: Agent unreachable — holding connection, agent-lost watchdog armed");
                ShowToast(() => Notification.ShowWarning(
                    Loc.T("Toast_AgentUnreachable")));
            }
            else
            {
                Logger.Info("Adaptive Agent Safety Monitor: Agent reachable again");
            }
        }

        /// <summary>Un tick di /status: il decision engine decide se transitare unsafe/safe.</summary>
        private void OnStatusUpdated(AgentStatusSnapshot snap)
        {
            if (!Connected) { return; }

            var decision = _engine.Evaluate(snap, _settings);
            if (_engine.JudgeChangedThisTick)
            {
                if (_engine.GuideJudgeActive)
                {
                    Logger.Info("Adaptive Agent Safety Monitor: the guide channel judges the sky");
                }
                else
                {
                    // §128 — l'Agente non puo' fare da giudice (versione vecchia, sensore del
                    // cielo spento). Non c'e' piu' il ripiego sulla camera di ripresa: le nubi
                    // non vengono giudicate, e lo si dice ad alta voce.
                    Logger.Warning("Adaptive Agent Safety Monitor: the running Agent cannot judge the sky "
                                   + "(older than 3.1, or its sky sensor is off) — clouds are NOT judged; "
                                   + "lost star, silent guide channel and lost Agent still are");
                    ShowToast(() => Notification.ShowWarning(Loc.T("Toast_SkyJudgeUnavailable")));
                }
            }
            switch (decision)
            {
                case SafetyDecision.BecameUnsafe:
                    _internalSafe = false;
                    switch (_engine.LastCause)
                    {
                        case SafetyCause.Cloud:
                            Logger.Info("Adaptive Agent Safety Monitor: UNSAFE — guide-star signal collapsed "
                                        + "below half of its clear-sky reference (clouds, or a guide-camera fault), "
                                        + "judged by the guide channel");
                            ShowToast(() => Notification.ShowWarning(
                                Loc.T("Toast_GuideSkyUnsafe")));
                            break;
                        case SafetyCause.GuideUnobservable:
                            Logger.Info("Adaptive Agent Safety Monitor: UNSAFE — guide channel unobservable "
                                        + "(PHD2 stopped delivering guide frames while guiding was expected)");
                            ShowToast(() => Notification.ShowWarning(
                                Loc.T("Toast_GuideUnobservable")));
                            break;

                        case SafetyCause.AgentLost:
                            Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — Adaptive Agent unreachable for {_settings.AgentLostUnsafePolls} polls during an active session");
                            ShowToast(() => Notification.ShowWarning(
                                Loc.T("Toast_AgentLostUnsafe")));
                            break;
                        default:
                            var secs = _settings.StarLostConsolidationSeconds;
                            var dur = secs >= 60 ? string.Format(Loc.T("Unit_Minutes"), secs / 60) : $"{secs}s";
                            Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — STAR_LOST sustained for {dur}");
                            ShowToast(() => Notification.ShowWarning(
                                string.Format(Loc.T("Toast_StarLostUnsafe"), dur)));
                            break;
                    }
                    break;

                case SafetyDecision.BecameSafe:
                    _internalSafe = true;
                    Logger.Info(_engine.GuideJudgeActive
                        ? "Adaptive Agent Safety Monitor: SAFE — conditions recovered (confirmed by the guide channel)"
                        : "Adaptive Agent Safety Monitor: SAFE — conditions recovered");
                    ShowToast(() => Notification.ShowInformation(
                        Loc.T("Toast_RecoveredSafe")));
                    break;

                case SafetyDecision.NoChange:
                default:
                    break;
            }

            // §72 — protezione meridiano: il valore RIPORTATO puo' divergere da quello
            // interno SOLO dentro la finestra (limitata, osservabile, revocabile).
            var windowOpen = TickMeridianProtection();
            IsSafe = _internalSafe || windowOpen;

            // §73 — riflesso verso la dashboard: uno stato VERO per volta, con la
            // causa (che e' l'azione operativa distinta) e un dettaglio leggibile.
            PublishState(windowOpen);

            // §126 — sulle TRANSIZIONI la riga dei contatori va a Info: la forense del
            // 3-4/10 ha dovuto DEDURRE i contatori da orari e default, perche' la riga
            // per tick e' Debug e il log di NINA era a Info.
            if (decision != SafetyDecision.NoChange)
            {
                Logger.Info($"Adaptive Agent Safety Monitor: transition {decision} — {_engine.LastTickSummary}");
            }

            // §3 osservabilita' — una riga per tick, stato POST-decisione (Debug: segue il
            // livello di log globale di NINA, come da convenzioni plugin).
            Logger.Debug($"N6 tick: {_engine.LastTickSummary} "
                         + $"| meridian[{(windowOpen ? "OPEN" : "idle")}] "
                         + $"-> internal={(_internalSafe ? "SAFE" : "UNSAFE")} reported={(IsSafe ? "SAFE" : "UNSAFE")} ({decision})");
        }

        /// <summary>
        /// §73 — traduce lo stato interno in UNO stato riportato per la dashboard.
        /// Nessuno stato inventato: SAFE / UNSAFE(+causa) / MERIDIAN_PROTECTION sono
        /// esattamente cio' che il monitor puo' essere. La finestra §72 ha precedenza
        /// di VISUALIZZAZIONE perche' e' l'unico caso in cui il riportato diverge
        /// dall'interno — ed e' proprio quello che l'osservatore deve vedere.
        /// </summary>
        private void PublishState(bool windowOpen)
        {
            try
            {
                string state;
                string? cause = null;
                string? detail = null;

                if (windowOpen)
                {
                    // §73 — nessun testo utente da qui: il plugin invia FATTI (stato,
                    // causa), la dashboard mette le PAROLE. Altrimenti una stringa
                    // localizzata del plugin (EN/IT) finirebbe in una dashboard che ha
                    // una lingua sua: due sistemi di localizzazione sullo stesso testo.
                    state = "MERIDIAN_PROTECTION";
                }
                else if (_internalSafe)
                {
                    state = "SAFE";
                }
                else
                {
                    state = "UNSAFE";
                    // §126-bis — la causa ATTUALE: dopo una stella ritrovata il latch e'
                    // passato al cielo della guida e la dashboard deve dirlo.
                    var attuale = _engine.CurrentCause != SafetyCause.None ? _engine.CurrentCause : _engine.LastCause;
                    cause = attuale switch
                    {
                        SafetyCause.Cloud => "CLOUD",
                        SafetyCause.AgentLost => "AGENT_LOST",
                        SafetyCause.GuideUnobservable => "GUIDE_UNOBSERVABLE",
                        SafetyCause.StarLost => "STAR_LOST",
                        _ => null,
                    };
                }
                _publisher.Publish(state, cause, detail, Connected, _internalSafe,
                                   _settings.HealthCheckIntervalSeconds, JudgeName());
            }
            catch (Exception ex)
            {
                // La presentazione non puo' MAI disturbare la sicurezza.
                Logger.Debug($"Safety state publish skipped ({ex.Message})");
            }
        }

        /// <summary>§126-bis/§128 — chi giudica il cielo, per l'Agente e la dashboard: "GUIDE",
        /// oppure "NONE" se l'Agente in esecuzione non puo' farlo (le nubi non sono giudicate).
        /// Finche' non e' deciso si assume la guida.</summary>
        private string JudgeName()
            => _engine.JudgeDecided && !_engine.GuideJudgeActive ? "NONE" : "GUIDE";

        /// <summary>
        /// §72 — un tick della protezione meridiano: legge montatura e profilo (fail-inert:
        /// qualunque cosa manchi => finestra Idle), fa girare la macchina a stati pura ed
        /// esegue le azioni (riattivazione tracking ex-post, log, toast).
        /// </summary>
        private bool TickMeridianProtection()
        {
            try
            {
                var services = AgentServices.Instance;
                var tm = services.TelescopeMediator;
                var info = tm?.GetInfo();

                bool mountConnected = info?.Connected == true;
                bool tracking = info?.TrackingEnabled == true;
                bool? pierIsWest = null;
                double? minutesToDeadline = null;

                if (mountConnected && info != null)
                {
                    pierIsWest = info.SideOfPier switch
                    {
                        NINA.Core.Enum.PierSide.pierWest => true,
                        NINA.Core.Enum.PierSide.pierEast => false,
                        _ => (bool?)null,
                    };
                    // Angolo orario in ORE (LST − RA, normalizzato a [−12, +12]);
                    // deadline del flip = meridiano + MaxMinutesAfterMeridian (profilo).
                    double ha = info.SiderealTime - info.RightAscension;
                    while (ha < -12) { ha += 24; }
                    while (ha > 12) { ha -= 24; }
                    double maxAfterMin = services.ProfileService?
                        .ActiveProfile?.MeridianFlipSettings?.MaxMinutesAfterMeridian ?? 5.0;
                    minutesToDeadline = maxAfterMin - ha * 60.0;
                }

                var (evt, resumeTracking) = _meridian.Tick(
                    _settings.MeridianProtectionEnabled, _internalSafe, mountConnected,
                    tracking, pierIsWest, minutesToDeadline, _settings.MeridianLeadMinutes,
                    Environment.TickCount64 / 1000.0,
                    _ninaFlipInProgress, Volatile.Read(ref _ninaFlipsCompleted));

                if (resumeTracking && tm != null)
                {
                    Logger.Warning("Meridian protection (§72): tracking was already stopped by the "
                                   + "unsafe-flip guard — re-enabling it INSIDE the window so the "
                                   + "delayed flip can run at the next trigger evaluation");
                    tm.SetTrackingEnabled(true);
                }

                switch (evt)
                {
                    case MeridianEvent.Opened:
                        Logger.Info($"Meridian protection (§72): window OPEN "
                                    + $"(deadline in {minutesToDeadline:0.0} min, lead {_settings.MeridianLeadMinutes} min) "
                                    + "— reporting SAFE for the mechanical flip ONLY; internal latches untouched");
                        ShowToast(() => Notification.ShowInformation(Loc.T("Toast_MeridianOpen")));
                        break;
                    case MeridianEvent.OpenedDuringFlip:
                        Logger.Info("Meridian protection (§72-bis): window OPEN for a NINA flip already in "
                                    + "progress — reporting SAFE until NINA finishes it (recenter and guiding "
                                    + "included); internal latches untouched");
                        ShowToast(() => Notification.ShowInformation(Loc.T("Toast_MeridianOpen")));
                        break;
                    case MeridianEvent.ClosedFlipDone:
                        Logger.Info("Meridian protection (§72): flip completed (NINA flip finished, or pier side "
                                    + "changed without a NINA flip) — window CLOSED, honest state restored "
                                    + "(recovery loop re-parks the sequence if still unsafe)");
                        ShowToast(() => Notification.ShowInformation(Loc.T("Toast_MeridianFlipDone")));
                        break;
                    case MeridianEvent.ClosedTimeout:
                        Logger.Warning($"Meridian protection (§72): window expired — no flip within "
                                       + $"{MeridianProtectionEngine.WindowMaxMinutes:0} min (trigger missing/disabled?) "
                                       + $"or a NINA flip still running after {MeridianProtectionEngine.FlipMaxMinutes:0} min "
                                       + "— LOCKOUT, check the mount (tracking may need a manual restart) when the sky clears");
                        ShowToast(() => Notification.ShowWarning(Loc.T("Toast_MeridianTimeout")));
                        break;
                    case MeridianEvent.ClosedConditionsLost:
                        Logger.Info("Meridian protection (§72): window closed (real safe / mount lost / disabled)");
                        break;
                }
                return _meridian.WindowOpen;
            }
            catch (Exception ex)
            {
                // Fail-inert: la protezione meridiano non deve MAI abbattere il tick N6.
                Logger.Error($"Meridian protection (§72) tick failed ({ex.Message}) — window forced closed");
                return false;
            }
        }
    }
}
