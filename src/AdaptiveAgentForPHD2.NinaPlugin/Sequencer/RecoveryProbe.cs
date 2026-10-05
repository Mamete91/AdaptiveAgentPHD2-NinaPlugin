#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Lifecycle;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>
    /// §57-ter — "Recovery probe (Adaptive Agent)": il CICLO AUTONOMO COMPLETO del
    /// recovery da UNSAFE-nubi, in una sola istruzione. Si mette DIRETTAMENTE dentro
    /// "Before Waiting For Safety" del Trigger On Unsafe — nessun container, nessuna
    /// condizione esterna:
    ///
    ///   while (monitor UNSAFE e sequenza non annullata):
    ///       attendi gate (S1 timeout / S2 hint, mai sotto min-interval)
    ///       scatta UNA posa-sonda (replica del light interrotto)  → forwarder → N1
    ///   (esce da solo quando il Safety Monitor torna SAFE)
    ///
    /// Storia del design (2 vincoli GUI provati sul campo, 13-14/7):
    ///  1. i container del Trigger On Unsafe RIFIUTANO le istruzioni Camera → la posa
    ///     e' interna all'istruzione (§57-bis);
    ///  2. rifiutano anche container/condizioni ("Loop While Unsafe" incluso) → il
    ///     ciclo e' interno all'istruzione (§57-ter, questa revisione).
    ///
    /// Confine di safety INVARIATO: l'istruzione LEGGE lo stato del monitor
    /// (ISafetyMonitorMediator.GetInfo(), come il WaitUntilSafe core) solo per capire
    /// QUANDO FERMARSI — non giudica e non imposta mai IsSafe: il ritorno a SAFE passa
    /// esclusivamente da sonda → N1 fresco → drain §55 → N6. Hint S2: puo' solo
    /// ANTICIPARE la sonda; agente offline ⇒ puro timeout S1.
    /// </summary>
    [ExportMetadata("Name", "Recovery probe (Adaptive Agent)")]
    [ExportMetadata("Description",
        "Recovery step for the Trigger On Unsafe ('Before Waiting For Safety'). Since plugin 1.14 the guide " +
        "channel judges the sky in both directions, so this instruction takes NO exposures: it keeps the " +
        "judge alive instead. While unsafe it restarts guiding if guiding has stopped, if the guide star has " +
        "been lost for longer than the minimum interval, or once per probe timeout of continued unsafe; it " +
        "ends on its own when the monitor returns SAFE. With the legacy option 'the imaging camera can also " +
        "report unsafe' it runs the old loop instead: unguided LIGHT verification exposures replicating the " +
        "interrupted sub, until the monitor returns SAFE.")]
    [ExportMetadata("Icon", "HourglassSVG")]
    [ExportMetadata("Category", "Adaptive Agent for PHD2")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RecoveryProbe : SequenceItem, IValidatable
    {
        private const double DefaultTimeoutMinutes = 12;       // [recovery_probe] probe_timeout_min
        private const double DefaultMinIntervalMinutes = 5;    // [recovery_probe] probe_min_interval_min
        private const double DefaultFallbackExposureSeconds = 60;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        // Ultima sonda autorizzata: condivisa tra iterazioni/istanze (floor paletto 3).
        private static DateTimeOffset _lastGateOpenUtc = DateTimeOffset.MinValue;

        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly ISafetyMonitorMediator _safetyMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly HttpClient _http;
        private readonly PluginSettings _settings;

        private bool _autoTimeout = true;   // §64 — automatico di default
        private double _timeoutMinutes = DefaultTimeoutMinutes;
        private double _minIntervalMinutes = DefaultMinIntervalMinutes;
        private double _fallbackExposureSeconds = DefaultFallbackExposureSeconds;
        private IList<string> _issues = new List<string>();

        [ImportingConstructor]
        public RecoveryProbe(IImagingMediator imagingMediator,
                             IImageSaveMediator imageSaveMediator,
                             ICameraMediator cameraMediator,
                             ISafetyMonitorMediator safetyMonitorMediator,
                             IGuiderMediator guiderMediator,
                             ITelescopeMediator telescopeMediator)
        {
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _cameraMediator = cameraMediator;
            _safetyMediator = safetyMonitorMediator;
            _guiderMediator = guiderMediator;
            _telescopeMediator = telescopeMediator;
            _settings = AgentServices.Instance.Settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        private RecoveryProbe(RecoveryProbe cloneMe)
            : this(cloneMe._imagingMediator, cloneMe._imageSaveMediator,
                   cloneMe._cameraMediator, cloneMe._safetyMediator,
                   cloneMe._guiderMediator, cloneMe._telescopeMediator)
        {
            CopyMetaData(cloneMe);
        }

        public override object Clone()
        {
            return new RecoveryProbe(this)
            {
                AutoTimeout = AutoTimeout,
                TimeoutMinutes = TimeoutMinutes,
                MinIntervalMinutes = MinIntervalMinutes,
                FallbackExposureSeconds = FallbackExposureSeconds,
            };
        }

        /// <summary>
        /// §64 — quando true (default) la cadenza fail-safe S1 e' CALCOLATA: agganciata alla
        /// finestra di freschezza §43 dell'agente meno la durata della posa-sonda, cosi' la
        /// telemetria di N1 non diventa mai stantia tra due sonde. Deselezionare per tornare
        /// al valore manuale (escape hatch: la logica adattiva e' in validazione sul campo).
        /// </summary>
        [JsonProperty]
        public bool AutoTimeout
        {
            get => _autoTimeout;
            set
            {
                _autoTimeout = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ManualTimeoutEnabled));
            }
        }

        /// <summary>Il campo manuale e' editabile solo a timeout automatico disattivato.</summary>
        public bool ManualTimeoutEnabled => !_autoTimeout;

        /// <summary>Cadenza fail-safe S1 MANUALE: usata solo se AutoTimeout e' false.</summary>
        [JsonProperty]
        public double TimeoutMinutes
        {
            get => _timeoutMinutes;
            set { _timeoutMinutes = Math.Clamp(value, 1, 120); RaisePropertyChanged(); }
        }

        /// <summary>Paletto 3: intervallo minimo ASSOLUTO tra due sonde (floor anche per l'hint).</summary>
        [JsonProperty]
        public double MinIntervalMinutes
        {
            get => _minIntervalMinutes;
            set { _minIntervalMinutes = Math.Clamp(value, 0, 60); RaisePropertyChanged(); }
        }

        /// <summary>Esposizione usata SOLO se nessun LIGHT e' ancora stato salvato in sessione.</summary>
        [JsonProperty]
        public double FallbackExposureSeconds
        {
            get => _fallbackExposureSeconds;
            set { _fallbackExposureSeconds = Math.Clamp(value, 1, 1800); RaisePropertyChanged(); }
        }

        public IList<string> Issues
        {
            get => _issues;
            private set { _issues = value; RaisePropertyChanged(); }
        }

        public bool Validate()
        {
            var issues = new List<string>();
            // §126 — con la guida giudice l'istruzione non scatta pose: serve la guida,
            // non la camera (§126-bis: e' lei a far ripartire la guida se si ferma).
            if (GuideJudge)
            {
                if (!_guiderMediator.GetInfo().Connected)
                {
                    issues.Add(Loc.T("Probe_Issue_Guider"));
                }
            }
            else if (!_cameraMediator.GetInfo().Connected)
            {
                issues.Add(Loc.T("Probe_Issue_Camera"));
            }
            var safety = _safetyMediator.GetInfo();
            if (!safety.Connected)
            {
                issues.Add(Loc.T("Probe_Issue_Safety"));
            }
            Issues = issues;
            return issues.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // §126 — con la guida giudice del cielo il ritorno a SAFE non passa piu' da
            // una posa: la sonda non serve e ritarderebbe la ripresa (nei 6 rientri
            // storici con una sonda in posa il sub e' ripartito da 8 a 632 s dopo il
            // SAFE). §126-bis — l'istruzione diventa la SONDA DI GUIDA: se durante
            // l'UNSAFE la guida e' ferma, o la stella e' persa da troppo, o l'UNSAFE
            // dura oltre la cadenza di sicurezza, fa RIPARTIRE la guida. Senza, a guida
            // ferma nessuno poteva piu' riportare SAFE (verifica del 05/10).
            if (GuideJudge)
            {
                bool versoCamera = await GuideProbeLoopAsync(progress, token).ConfigureAwait(false);
                if (!versoCamera) { return; }
                Logger.Info("RecoveryProbe: the imaging camera judges the sky again — switching to probe exposures");
            }

            // §57-ter — CICLO COMPLETO: (attesa gate → sonda) ripetuto finche' il monitor
            // e' UNSAFE. Esce quando: SAFE (percorso normale) o cancellazione (sequenza
            // annullata / chiusura NINA). Lo stato safety viene LETTO ad ogni poll (5 s):
            // un SAFE che arriva a meta' attesa interrompe subito, come faceva il
            // watchdog del Loop While Unsafe nel design precedente.
            var minInterval = TimeSpan.FromMinutes(MinIntervalMinutes);
            int probes = 0;
            bool failureToastShown = false;
            Logger.Info($"RecoveryProbe: recovery loop started ({(AutoTimeout
                            ? "timeout AUTO — matched to the Agent's telemetry freshness window (§43)"
                            : $"timeout {TimeoutMinutes:0.#} min (manual)")}, " +
                        $"min-interval {MinIntervalMinutes:0.#} min)");

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (IsSafeNow())
                {
                    Logger.Info($"RecoveryProbe: safety monitor reports SAFE — recovery loop ends ({probes} probe(s) attempted)");
                    return;
                }

                // ---- Fase 1: gate temporale (S1 timeout / S2 hint, floor min-interval) ----
                var start = DateTimeOffset.UtcNow;
                string gateReason;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSafeNow())
                    {
                        Logger.Info($"RecoveryProbe: SAFE returned while waiting — recovery loop ends ({probes} probe(s) attempted)");
                        return;
                    }
                    var now = DateTimeOffset.UtcNow;
                    var (hint, windowSeconds, channelReady) = await ReadAgentStatusAsync().ConfigureAwait(false);
                    // §64 — ricalcolata a ogni poll: la posa replicata puo' cambiare
                    // (filtro/target diversi) e la finestra §43 la segue.
                    var timeout = EffectiveTimeout(windowSeconds);
                    // §71 — kill-switch: disattivato => null => fail-open (pre-§71).
                    var gateReady = _settings.ProbeChannelGateEnabled ? channelReady : null;
                    var (open, reason) = RecoveryProbeGate.Evaluate(
                        now - start, now - _lastGateOpenUtc, hint, timeout, minInterval, gateReady);
                    if (open)
                    {
                        _lastGateOpenUtc = now;
                        gateReason = reason;
                        break;
                    }
                    progress?.Report(new ApplicationStatus { Status = $"Recovery probe: {reason}" });
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                }

                // ---- Fase 2: posa-sonda (replica del light interrotto) ----
                // §57-ter hardening: un guasto di cattura (camera/USB/driver) NON deve
                // terminare il ciclo — terminava l'intero recovery e riapriva l'attesa
                // muta proprio nello scenario peggiore. Si logga, si avvisa (toast solo al
                // PRIMO fallimento: niente spam ogni gate con una camera morta) e si
                // riprova al prossimo gate. La CANCELLAZIONE resta prioritaria e propaga.
                probes++;
                try
                {
                    await TakeProbeAsync(gateReason, probes, progress, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;   // sequenza annullata / chiusura NINA: il loop DEVE fermarsi
                }
                catch (Exception ex)
                {
                    Logger.Warning($"RecoveryProbe: probe #{probes} FAILED ({ex.Message}) — " +
                                   "recovery loop continues, next attempt at the next gate");
                    if (!failureToastShown)
                    {
                        failureToastShown = true;
                        ToastHelper.Show(() => NINA.Core.Utility.Notification.Notification.ShowWarning(
                            string.Format(Loc.T("Toast_ProbeFailed"), ex.Message)));
                    }
                }
                // Il forwarder POSTa la sonda a N1; il drain §55 impiega ~1 min a riportare
                // SAFE se il cielo e' davvero tornato: il prossimo giro di attesa (gated dal
                // min-interval) legge lo stato e esce.
            }
        }

        /// <summary>§126-bis — il giudice effettivo del monitor (l'opzione finche' non e' deciso).</summary>
        private bool GuideJudge
        {
            get
            {
                var engine = AgentServices.Instance.SafetyEngine.Value;
                return engine.JudgeDecided ? engine.GuideJudgeActive : !_settings.ImagingCameraUnsafeEnabled;
            }
        }

        /// <summary>
        /// §126-bis — la sonda di guida. Non giudica e non imposta IsSafe: tiene viva la
        /// GUIDA, che e' il giudice. Tre casi, uno alla volta, mai piu' spesso di
        /// MinIntervalMinutes (lo stesso intervallo minimo delle pose-sonda):
        ///   • guida NON attesa da almeno MinInterval → StartGuiding;
        ///   • stella persa da almeno MinInterval → StopGuiding + StartGuiding: dopo una
        ///     nube lunga la stella puo' essere uscita dalla regione di ricerca di PHD2
        ///     (15 px), che non la ritrova piu' da solo;
        ///   • UNSAFE da oltre la cadenza di sicurezza (TimeoutMinutes) con la guida che
        ///     gira → un riavvio del canale: il 3/10 il guasto della camera di guida
        ///     (SNR 74→17) si e' risolto proprio a una ripartenza della guida.
        /// Ritorna true se nel frattempo il giudice e' tornato la camera.
        /// </summary>
        private async Task<bool> GuideProbeLoopAsync(IProgress<ApplicationStatus>? progress, CancellationToken token)
        {
            // §126-quater — con la guida giudice l'intervallo minimo e' l'unico freno ai
            // riavvii: mai sotto 1 minuto (0 era ammesso e dava una raffica).
            var minInterval = TimeSpan.FromMinutes(Math.Max(1.0, MinIntervalMinutes));
            var cadenza = TimeSpan.FromMinutes(TimeoutMinutes);
            var inizio = DateTimeOffset.UtcNow;
            var ultimoRiavvio = inizio;
            DateTimeOffset? nonAttesaDa = null;
            bool toastMostrato = false;
            Logger.Info("RecoveryProbe: the guide channel judges the sky (plugin 1.14) — no probe exposures; "
                        + $"guiding is restarted if it stops (min interval {MinIntervalMinutes:0.#} min, "
                        + $"channel restart every {TimeoutMinutes:0.#} min of unsafe)");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (IsSafeNow())
                {
                    Logger.Info("RecoveryProbe: safety monitor reports SAFE — waiting ends (no probe taken)");
                    return false;
                }
                if (!GuideJudge) { return true; }

                var (attesa, persa, persaDa) = await ReadGuideStatusAsync().ConfigureAwait(false);
                var ora = DateTimeOffset.UtcNow;
                bool liberoDaIntervallo = ora - _lastGateOpenUtc >= minInterval;
                string? motivo = null;
                bool fermaPrima = false;
                if (attesa == false)
                {
                    nonAttesaDa ??= ora;
                    if (ora - nonAttesaDa.Value >= minInterval && liberoDaIntervallo)
                    {
                        motivo = "guiding is not running";
                    }
                }
                else
                {
                    nonAttesaDa = null;
                    if (persa == true && (persaDa ?? 0) >= minInterval.TotalSeconds && liberoDaIntervallo)
                    {
                        motivo = $"guide star lost for {persaDa:0} s";
                        fermaPrima = true;
                    }
                    else if (attesa == true && ora - ultimoRiavvio >= cadenza && liberoDaIntervallo)
                    {
                        motivo = $"still unsafe after {cadenza.TotalMinutes:0.#} min — restarting the guide channel";
                        fermaPrima = true;
                    }
                }

                if (motivo != null)
                {
                    _lastGateOpenUtc = ora;
                    ultimoRiavvio = ora;
                    nonAttesaDa = null;
                    try
                    {
                        await RestartGuidingAsync(motivo, fermaPrima, progress, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"RecoveryProbe: restarting guiding FAILED ({ex.Message}) — "
                                       + "will retry after the minimum interval");
                        if (!toastMostrato)
                        {
                            toastMostrato = true;
                            ToastHelper.Show(() => NINA.Core.Utility.Notification.Notification.ShowWarning(
                                string.Format(Loc.T("Toast_GuideProbeFailed"), ex.Message)));
                        }
                    }
                }
                else
                {
                    progress?.Report(new ApplicationStatus
                    {
                        Status = "Recovery probe: the guide channel is watching the sky — waiting for SAFE"
                    });
                }
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }

        private async Task RestartGuidingAsync(string motivo, bool fermaPrima,
                                               IProgress<ApplicationStatus>? progress, CancellationToken token)
        {
            // §126-quater — il monitor puo' essere tornato SAFE mentre si attendeva il
            // turno: in quel caso non si tocca niente.
            if (IsSafeNow()) { return; }
            var tel = _telescopeMediator.GetInfo();
            if (!tel.Connected || tel.AtPark || !tel.TrackingEnabled || tel.Slewing)
            {
                // Montatura scollegata, parcheggiata, ferma o in movimento: far ripartire
                // la guida non serve o disturba (§72: la guardia di NINA puo' aver
                // fermato il tracking alla scadenza del flip).
                Logger.Warning("RecoveryProbe: mount not ready (connected=" + tel.Connected
                               + ", parked=" + tel.AtPark + ", tracking=" + tel.TrackingEnabled
                               + ", slewing=" + tel.Slewing + ") — guiding NOT restarted");
                return;
            }
            if (!_guiderMediator.GetInfo().Connected)
            {
                Logger.Warning("RecoveryProbe: guider not connected — guiding NOT restarted");
                return;
            }
            Logger.Warning($"RecoveryProbe: {motivo} — restarting guiding so the guide channel can judge the sky");
            progress?.Report(new ApplicationStatus { Status = "Recovery probe: restarting guiding" });
            if (fermaPrima)
            {
                await _guiderMediator.StopGuiding(token).ConfigureAwait(false);
            }
            bool ok = await _guiderMediator.StartGuiding(false, progress, token).ConfigureAwait(false);
            if (!ok) { throw new InvalidOperationException("start guiding did not succeed"); }
            Logger.Info("RecoveryProbe: guiding restarted — the guide channel will confirm the sky");
        }

        /// <summary>§126-bis — GET /status → (guida attesa, stella persa, da quanti secondi).
        /// Graceful: qualunque errore => (null, null, null) e nessuna azione.</summary>
        private async Task<(bool? Attesa, bool? Persa, double? PersaDa)> ReadGuideStatusAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/status";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return (null, null, null); }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("guide_health", out var gh)
                    || gh.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return (null, null, null);
                }
                bool? attesa = null, persa = null;
                double? persaDa = null;
                if (gh.TryGetProperty("guiding_expected", out var ge))
                {
                    if (ge.ValueKind == System.Text.Json.JsonValueKind.True) { attesa = true; }
                    else if (ge.ValueKind == System.Text.Json.JsonValueKind.False) { attesa = false; }
                }
                if (gh.TryGetProperty("star_lost", out var sl))
                {
                    if (sl.ValueKind == System.Text.Json.JsonValueKind.True) { persa = true; }
                    else if (sl.ValueKind == System.Text.Json.JsonValueKind.False) { persa = false; }
                }
                if (gh.TryGetProperty("star_lost_s", out var sls) && sls.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    persaDa = sls.GetDouble();
                }
                return (attesa, persa, persaDa);
            }
            catch
            {
                return (null, null, null);
            }
        }

        /// <summary>Lettura (mai scrittura) dello stato del Safety Monitor — come il WaitUntilSafe core.</summary>
        private bool IsSafeNow()
        {
            try
            {
                var info = _safetyMediator.GetInfo();
                return info.Connected && info.IsSafe;
            }
            catch
            {
                return false;   // stato ignoto => continua il recovery (conservativo)
            }
        }

        private async Task TakeProbeAsync(string gateReason, int probeNumber,
                                          IProgress<ApplicationStatus>? progress, CancellationToken token)
        {
            var profile = LastLightMemory.Current;
            double exposure = profile?.ExposureSeconds ?? FallbackExposureSeconds;
            var capture = new CaptureSequence
            {
                ExposureTime = exposure,
                ImageType = CaptureSequence.ImageTypes.LIGHT,
                TotalExposureCount = 1,
            };
            if (profile != null)
            {
                capture.Gain = profile.Gain;
                capture.Offset = profile.Offset;
                capture.Binning = new NINA.Core.Model.Equipment.BinningMode(profile.BinX, profile.BinY);
            }
            // NB: nessun FilterType => nessun comando alla ruota (il filtro del sub e' gia' in posizione).

            // Telemetria paletto 8 (lato plugin): trigger + parametri della sonda.
            Logger.Info($"RecoveryProbe: gate OPEN — {gateReason} -> probe #{probeNumber}: {exposure:0.#}s LIGHT " +
                        (profile != null
                            ? $"(replica of last light: gain={profile.Gain} offset={profile.Offset} bin={profile.BinX}x{profile.BinY} filter={profile.Filter ?? "-"})"
                            : "(no light seen this session: fallback exposure)"));

            progress?.Report(new ApplicationStatus { Status = $"Recovery probe #{probeNumber}: exposing {exposure:0.#}s..." });
            var exposureData = await _imagingMediator.CaptureImage(capture, token, progress).ConfigureAwait(false);
            var imageData = await exposureData.ToImageData(progress, token).ConfigureAwait(false);
            var prepareTask = _imagingMediator.PrepareImage(imageData, new PrepareImageParameters(null, true), token);
            // Salvataggio via pipeline standard => ImageSaved => forwarder §42 => N1 fresco.
            await _imageSaveMediator.Enqueue(imageData, prepareTask, progress, token).ConfigureAwait(false);
            Logger.Info($"RecoveryProbe: probe #{probeNumber} saved — the Agent's transparency index will refresh on ingest");
        }

        /// <summary>§64 — timeout effettivo: AUTO (finestra §43 − posa) oppure manuale.</summary>
        private TimeSpan EffectiveTimeout(double? agentWindowSeconds)
        {
            if (!AutoTimeout) { return TimeSpan.FromMinutes(TimeoutMinutes); }
            var exposure = LastLightMemory.Current?.ExposureSeconds ?? FallbackExposureSeconds;
            return RecoveryProbeGate.AdaptiveTimeout(agentWindowSeconds, exposure);
        }

        /// <summary>
        /// GET /status → (recovery_hint.active, nina.transparency.window_s).
        /// Graceful: qualunque errore => (false, null) — puro S1 con finestra di fallback.
        /// </summary>
        private async Task<(bool HintActive, double? WindowSeconds, bool? ChannelReady)> ReadAgentStatusAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/status";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return (false, null, null); }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool hint = root.TryGetProperty("recovery_hint", out var rh)
                    && rh.ValueKind == System.Text.Json.JsonValueKind.Object
                    && rh.TryGetProperty("active", out var act)
                    && act.ValueKind == System.Text.Json.JsonValueKind.True;

                double? window = null;
                if (root.TryGetProperty("nina", out var nina)
                    && nina.ValueKind == System.Text.Json.JsonValueKind.Object
                    && nina.TryGetProperty("transparency", out var transp)
                    && transp.ValueKind == System.Text.Json.JsonValueKind.Object
                    && transp.TryGetProperty("window_s", out var w)
                    && w.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    window = w.GetDouble();
                }

                // §71 — "canale pronto" (consenso multi-condizione §68, calcolato
                // dall'Agente a risoluzione 3 s). Tollerante: blocco assente
                // (Agente <v2.10) => null => fail-open nel gate.
                bool? channelReady = null;
                if (root.TryGetProperty("guide_health", out var gh)
                    && gh.ValueKind == System.Text.Json.JsonValueKind.Object
                    && gh.TryGetProperty("channel_ready", out var cr))
                {
                    if (cr.ValueKind == System.Text.Json.JsonValueKind.True) { channelReady = true; }
                    else if (cr.ValueKind == System.Text.Json.JsonValueKind.False) { channelReady = false; }
                }
                return (hint, window, channelReady);
            }
            catch
            {
                return (false, null, null);
            }
        }

        public override TimeSpan GetEstimatedDuration()
        {
            // Durata reale = finche' torna SAFE (non prevedibile): stima di UN giro.
            var exposure = LastLightMemory.Current?.ExposureSeconds ?? FallbackExposureSeconds;
            return EffectiveTimeout(null) + TimeSpan.FromSeconds(exposure);
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(RecoveryProbe)}, " +
            $"Timeout: {(AutoTimeout ? "auto" : $"{TimeoutMinutes} min")}, " +
            $"MinInterval: {MinIntervalMinutes} min, FallbackExposure: {FallbackExposureSeconds}s";
    }
}
