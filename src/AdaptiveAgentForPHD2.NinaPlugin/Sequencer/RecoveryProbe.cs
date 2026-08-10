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
        "Self-contained cloud-recovery loop. Place it directly inside 'Before Waiting For Safety' of the " +
        "Trigger On Unsafe — no extra containers needed. While the safety monitor reports unsafe, it waits " +
        "(probe timeout as fail-safe, or earlier when the Adaptive Agent's guide-star SNR hints the sky is " +
        "recovering — never sooner than the minimum interval) and takes ONE unguided LIGHT verification " +
        "exposure replicating the interrupted sub. The saved probe refreshes the Agent's transparency index — " +
        "the only path back to safe. The loop ends on its own the moment the monitor returns SAFE.")]
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
                             ISafetyMonitorMediator safetyMonitorMediator)
        {
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _cameraMediator = cameraMediator;
            _safetyMediator = safetyMonitorMediator;
            _settings = AgentServices.Instance.Settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        private RecoveryProbe(RecoveryProbe cloneMe)
            : this(cloneMe._imagingMediator, cloneMe._imageSaveMediator,
                   cloneMe._cameraMediator, cloneMe._safetyMediator)
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
            var cam = _cameraMediator.GetInfo();
            if (!cam.Connected)
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
