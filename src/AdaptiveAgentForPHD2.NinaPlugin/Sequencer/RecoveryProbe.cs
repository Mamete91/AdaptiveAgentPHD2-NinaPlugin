#nullable enable
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
    /// §57-bis — "Recovery probe (Adaptive Agent)": l'istruzione AUTOCONTENUTA del
    /// recovery da UNSAFE-nubi. Attende il gate temporale (S1 timeout / S2 hint, mai
    /// sotto min-interval) e POI scatta internamente la posa-sonda, replicando il light
    /// interrotto (esposizione/gain/offset/binning dall'ultimo LIGHT salvato; filtro =
    /// ruota gia' in posizione, nessun comando). La sonda passa dal pipeline di
    /// salvataggio standard => ImageSaved => forwarder §42 => N1 fresco => drain §55
    /// => SAFE, e il Loop While Unsafe del template esce da solo.
    ///
    /// Nato dal vincolo GUI scoperto in validazione (13/7): i container del Trigger On
    /// Unsafe rifiutano le istruzioni di categoria Camera => il TakeExposure esterno del
    /// template v1 non era montabile. Qui l'imaging avviene DENTRO un'istruzione eseguita
    /// dal sequencer (stesso mecccanismo del TakeExposure core): il principio
    /// "l'imaging resta al sequencer" e' rispettato.
    ///
    /// Confini (paletti §57 aggiornati al Gate §57-bis):
    ///  - Nessuna autorita' safety: l'uscita a SAFE resta del Loop While Unsafe
    ///    (che cancella questa istruzione via CancellationToken, anche a meta' posa).
    ///  - S2 (hint) puo' solo ANTICIPARE la sonda: agente offline => puro timeout S1.
    ///  - Nessuna cattura autonoma: si scatta solo quando il sequencer esegue Execute().
    /// </summary>
    [ExportMetadata("Name", "Recovery probe (Adaptive Agent)")]
    [ExportMetadata("Description",
        "Self-contained cloud-recovery probe. Waits until the probe timeout elapses (S1, fail-safe) or the Adaptive " +
        "Agent reports a sky-recovery hint from guide-star SNR (S2, accelerator) — never sooner than the minimum " +
        "interval — then takes ONE unguided LIGHT exposure replicating the interrupted sub (exposure/gain/offset/" +
        "binning from the last saved light; current filter). The saved probe refreshes the Agent's transparency " +
        "index, which is the only path back to safe. Place it inside a 'Loop while unsafe' container in the " +
        "Trigger On Unsafe.")]
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
        private readonly HttpClient _http;
        private readonly PluginSettings _settings;

        private double _timeoutMinutes = DefaultTimeoutMinutes;
        private double _minIntervalMinutes = DefaultMinIntervalMinutes;
        private double _fallbackExposureSeconds = DefaultFallbackExposureSeconds;
        private IList<string> _issues = new List<string>();

        [ImportingConstructor]
        public RecoveryProbe(IImagingMediator imagingMediator,
                             IImageSaveMediator imageSaveMediator,
                             ICameraMediator cameraMediator)
        {
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _cameraMediator = cameraMediator;
            _settings = AgentServices.Instance.Settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        private RecoveryProbe(RecoveryProbe cloneMe)
            : this(cloneMe._imagingMediator, cloneMe._imageSaveMediator, cloneMe._cameraMediator)
        {
            CopyMetaData(cloneMe);
        }

        public override object Clone()
        {
            return new RecoveryProbe(this)
            {
                TimeoutMinutes = TimeoutMinutes,
                MinIntervalMinutes = MinIntervalMinutes,
                FallbackExposureSeconds = FallbackExposureSeconds,
            };
        }

        /// <summary>Cadenza fail-safe S1: la sonda parte comunque dopo questo tempo.</summary>
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
                issues.Add("Camera not connected — the recovery probe cannot expose");
            }
            Issues = issues;
            return issues.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            // ---- Fase 1: gate temporale (S1 timeout / S2 hint, floor min-interval) ----
            var start = DateTimeOffset.UtcNow;
            var timeout = TimeSpan.FromMinutes(TimeoutMinutes);
            var minInterval = TimeSpan.FromMinutes(MinIntervalMinutes);
            Logger.Info($"RecoveryProbe: waiting (timeout {TimeoutMinutes:0.#} min, min-interval {MinIntervalMinutes:0.#} min)");

            string gateReason;
            while (true)
            {
                token.ThrowIfCancellationRequested();   // Loop While Unsafe taglia qui al ritorno del SAFE

                var now = DateTimeOffset.UtcNow;
                bool hint = await ReadHintActiveAsync().ConfigureAwait(false);
                var (open, reason) = RecoveryProbeGate.Evaluate(
                    now - start, now - _lastGateOpenUtc, hint, timeout, minInterval);
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
            Logger.Info($"RecoveryProbe: gate OPEN — {gateReason} (waited {(DateTimeOffset.UtcNow - start).TotalMinutes:0.0} min) " +
                        $"-> probing {exposure:0.#}s LIGHT " +
                        (profile != null
                            ? $"(replica of last light: gain={profile.Gain} offset={profile.Offset} bin={profile.BinX}x{profile.BinY} filter={profile.Filter ?? "-"})"
                            : "(no light seen this session: fallback exposure)"));

            progress?.Report(new ApplicationStatus { Status = $"Recovery probe: exposing {exposure:0.#}s..." });
            var exposureData = await _imagingMediator.CaptureImage(capture, token, progress).ConfigureAwait(false);
            var imageData = await exposureData.ToImageData(progress, token).ConfigureAwait(false);
            var prepareTask = _imagingMediator.PrepareImage(imageData, new PrepareImageParameters(null, true), token);
            // Salvataggio via pipeline standard => ImageSaved => forwarder §42 => N1 fresco.
            await _imageSaveMediator.Enqueue(imageData, prepareTask, progress, token).ConfigureAwait(false);
            Logger.Info("RecoveryProbe: probe saved — the Agent's transparency index will refresh on ingest");
        }

        /// <summary>GET /status → recovery_hint.active. Graceful: qualunque errore => false (puro S1).</summary>
        private async Task<bool> ReadHintActiveAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/status";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return false; }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("recovery_hint", out var rh)
                    && rh.ValueKind == System.Text.Json.JsonValueKind.Object
                    && rh.TryGetProperty("active", out var act)
                    && act.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }

        public override TimeSpan GetEstimatedDuration()
        {
            var exposure = LastLightMemory.Current?.ExposureSeconds ?? FallbackExposureSeconds;
            return TimeSpan.FromMinutes(TimeoutMinutes) + TimeSpan.FromSeconds(exposure);
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(RecoveryProbe)}, Timeout: {TimeoutMinutes} min, " +
            $"MinInterval: {MinIntervalMinutes} min, FallbackExposure: {FallbackExposureSeconds}s";
    }
}
