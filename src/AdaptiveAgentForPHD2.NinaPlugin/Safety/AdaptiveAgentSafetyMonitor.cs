#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
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

        private bool _isSafe = true;   // ottimistico all'avvio: safe finche' non si dimostra il contrario
        private bool _connected;
        private bool _subscribed;

        public AdaptiveAgentSafetyMonitor(PluginSettings settings, AgentHealthChecker health, SafetyDecisionEngine engine)
        {
            _settings = settings;
            _health = health;
            _engine = engine;
        }

        // --- Identita' del driver (vedi pre-flight: stesse convenzioni del SafetyMonitorSimulator di NINA) ---
        public string Name => "Adaptive Agent for PHD2 — Guide Safety";
        public string DisplayName => Name;
        public string Description =>
            "Reflects the guiding and sky state of the Adaptive Agent for PHD2. Reports unsafe when " +
            "STAR_LOST persists beyond the configured timeout, when sky transparency stays degraded " +
            "(index-based persistence, v1.5), when NINA telemetry goes stale while the sky was degraded, " +
            "or when the Agent becomes unreachable during an active session. Losing reliable observation " +
            "is treated as a risk condition — never as \"safe\".";
        public string DriverInfo => "Adaptive Agent for PHD2 v1.5.0.0 — virtual Safety Monitor";
        public string DriverVersion => "1.5.0.0";
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
            Connected = false;
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
        }

        private void Unsubscribe()
        {
            if (!_subscribed) { return; }
            _subscribed = false;
            _health.StatusChanged -= OnHealthChanged;
            _health.StatusUpdated -= OnStatusUpdated;
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
                    "Adaptive Agent: Agent unreachable — Safety Monitor is watching (unsafe if it persists during an active session)"));
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
            switch (decision)
            {
                case SafetyDecision.BecameUnsafe:
                    IsSafe = false;
                    switch (_engine.LastCause)
                    {
                        case SafetyCause.Cloud:
                            Logger.Info("Adaptive Agent Safety Monitor: UNSAFE — persistent transparency degradation (clouds)");
                            ShowToast(() => Notification.ShowWarning(
                                "Adaptive Agent: persistent clouds (NINA transparency) — Safety Monitor unsafe"));
                            break;
                        case SafetyCause.StaleTelemetry:
                            Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — NINA telemetry stale for {_settings.StaleUnsafePolls} polls while last known sky was degraded");
                            ShowToast(() => Notification.ShowWarning(
                                "Adaptive Agent: telemetry went stale while the sky was degraded — Safety Monitor unsafe"));
                            break;
                        case SafetyCause.AgentLost:
                            Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — Adaptive Agent unreachable for {_settings.AgentLostUnsafePolls} polls during an active session");
                            ShowToast(() => Notification.ShowWarning(
                                "Adaptive Agent: Agent unreachable during an active session — Safety Monitor unsafe"));
                            break;
                        default:
                            var secs = _settings.StarLostConsolidationSeconds;
                            var dur = secs >= 60 ? $"{secs / 60} minutes" : $"{secs}s";
                            Logger.Info($"Adaptive Agent Safety Monitor: UNSAFE — STAR_LOST sustained for {dur}");
                            ShowToast(() => Notification.ShowWarning(
                                $"Adaptive Agent: guiding lost for {dur} — Safety Monitor unsafe"));
                            break;
                    }
                    break;

                case SafetyDecision.BecameSafe:
                    IsSafe = true;
                    Logger.Info("Adaptive Agent Safety Monitor: SAFE — conditions recovered");
                    ShowToast(() => Notification.ShowInformation(
                        "Adaptive Agent: conditions recovered — Safety Monitor safe"));
                    break;

                case SafetyDecision.NoChange:
                default:
                    break;
            }

            // §3 osservabilita' — una riga per tick, stato POST-decisione (Debug: segue il
            // livello di log globale di NINA, come da convenzioni plugin).
            Logger.Debug($"N6 tick: {_engine.LastTickSummary} -> {(IsSafe ? "SAFE" : "UNSAFE")} ({decision})");
        }
    }
}
