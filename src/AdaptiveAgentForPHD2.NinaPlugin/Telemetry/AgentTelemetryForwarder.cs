#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Mediator;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Telemetry
{
    /// <summary>
    /// §42 (Step 0 — lato plugin) — inoltra le metriche per-posa di NINA all'Agente
    /// Python: POST &lt;DashboardUrl&gt;/nina/telemetry, contratto §41 schema_version=1
    /// (solo blocco <c>image{}</c>; <c>context{}</c> è rimandato a N2).
    ///
    /// OPZIONALE e GRACEFUL — non deve MAI disturbare NINA:
    ///   • Agente offline / timeout / 5xx  -> swallow silenzioso (al più un log Debug);
    ///   • toggle <see cref="PluginSettings.ForwardTelemetryToAgent"/> = false -> no-op;
    ///   • handler <c>ImageSaved</c> non-throwing e veloce: estrae i campi e lancia il
    ///     POST come task fire-and-forget (NON awaita: non rallenta il salvataggio posa).
    /// Imita il pattern di <see cref="Health.AgentHealthChecker"/>: HttpClient riusato
    /// (no socket exhaustion), timeout 3s, mai un'eccezione verso il pipeline di imaging.
    ///
    /// NON è registrato in AgentServices (composition root statico, senza MEF): richiede
    /// <see cref="IImageSaveMediator"/>, iniettato via MEF nel costruttore del plugin, che
    /// quindi possiede il ciclo di vita del forwarder (Subscribe in Initialize / Dispose in Teardown).
    /// </summary>
    public sealed class AgentTelemetryForwarder : IDisposable
    {
        private const int SchemaVersion = 1;
        private const string Source = "nina-plugin";

        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly PluginSettings _settings;
        private readonly HttpClient _http;
        private bool _subscribed;
        private bool _disposed;

        public AgentTelemetryForwarder(IImageSaveMediator imageSaveMediator, PluginSettings settings)
        {
            _imageSaveMediator = imageSaveMediator;
            _settings = settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        /// <summary>Iscrizione idempotente a ImageSaved (chiamata in Plugin.Initialize).</summary>
        public void Subscribe()
        {
            if (_subscribed || _disposed || _imageSaveMediator == null) { return; }
            _imageSaveMediator.ImageSaved += OnImageSaved;
            _subscribed = true;
        }

        /// <summary>Disiscrizione idempotente (chiamata in Plugin.Teardown / Dispose).</summary>
        public void Unsubscribe()
        {
            if (!_subscribed || _imageSaveMediator == null) { return; }
            _imageSaveMediator.ImageSaved -= OnImageSaved;
            _subscribed = false;
        }

        // Handler veloce e NON-throwing. Qualunque errore è confinato qui: niente eccezioni
        // verso NINA, niente blocco del salvataggio della posa.
        private void OnImageSaved(object? sender, ImageSavedEventArgs e)
        {
            try
            {
                if (!_settings.ForwardTelemetryToAgent) { return; }   // kill-switch lato plugin
                if (e == null) { return; }

                var sda = e.StarDetectionAnalysis;
                if (sda == null) { return; }                          // niente star detection -> skip
                // Frame senza detection reale (es. detection spenta) -> niente payload spazzatura.
                if (sda.DetectedStars <= 0 && !(sda.HFR > 0)) { return; }

                var json = BuildPayload(e);
                _ = PostAsync(json);                                  // fire-and-forget
            }
            catch (Exception ex)
            {
                Logger.Debug($"AgentTelemetryForwarder: handler ImageSaved ignorato ({ex.Message})");
            }
        }

        // Mapping eventargs -> contratto §41 (tabella verificata sul sorgente NINA).
        // Costruisce solo il blocco image{}; i campi non finiti/negativi o le statistiche
        // assenti vengono OMESSI (l'Agente §41 tollera i campi mancanti).
        private string BuildPayload(ImageSavedEventArgs e)
        {
            var sda = e.StarDetectionAnalysis;

            var image = new Dictionary<string, object?>();
            AddIfNumber(image, "hfr", sda.HFR);                 // px
            AddIfNumber(image, "hfr_std", sda.HFRStDev);        // px
            if (sda.DetectedStars >= 0) { image["star_count"] = sda.DetectedStars; }
            AddIfNumber(image, "exposure_s", e.Duration);       // s
            // NB: FWHM (arcsec) ed Eccentricity NON sono esposti da IStarDetectionAnalysis
            // in NINA 3.2.0.9001 (aggiunti in build successive, ramo develop -> 3.3):
            // verificato dal compilatore contro l'SDK installato. Vengono quindi OMESSI
            // (non inventati). Il campo `fwhm` del contratto §41 resta forward-ready: appena
            // la NINA installata li espone, basta riaggiungere qui le due righe.

            // Statistiche ADU (proxy SNR/fondo). Statistics può essere null.
            var stats = e.Statistics;
            if (stats != null)
            {
                AddIfNumber(image, "mean_adu", stats.Mean);
                AddIfNumber(image, "median_adu", stats.Median);
                AddIfNumber(image, "stdev_adu", stats.StDev);
            }

            if (!string.IsNullOrEmpty(e.Filter)) { image["filter"] = e.Filter; }

            var payload = new Dictionary<string, object?>
            {
                ["schema_version"] = SchemaVersion,
                ["source"] = Source,
                ["ts_unix"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["image"] = image,
            };
            return JsonSerializer.Serialize(payload);
        }

        // Aggiunge il campo solo se finito e >= 0 (evita NaN/Infinity -> eccezione di
        // serializzazione, e valori negativi che l'Agente rifiuterebbe con 422).
        private static void AddIfNumber(IDictionary<string, object?> map, string key, double value)
        {
            if (double.IsFinite(value) && value >= 0) { map[key] = value; }
        }

        private async Task PostAsync(string json)
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/nina/telemetry";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
                // L'esito non interessa: l'Agente è opzionale. Nessun retry (la prossima
                // posa riprova naturalmente). Niente log sul percorso felice.
            }
            catch (Exception ex)
            {
                // Agente offline / connection refused / timeout / 5xx -> ignorato.
                Logger.Debug($"AgentTelemetryForwarder: POST telemetria fallito ({ex.Message}) — ignorato");
            }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Unsubscribe();
            _http.Dispose();
        }
    }
}
