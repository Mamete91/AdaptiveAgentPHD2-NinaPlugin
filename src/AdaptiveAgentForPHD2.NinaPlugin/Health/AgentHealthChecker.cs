#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Core.Utility;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Health
{
    /// <summary>Stato sintetico dell'Agente. Version e' null quando offline o senza campo.</summary>
    public sealed record AgentHealth(bool IsOnline, string? Version);

    /// <summary>
    /// Snapshot minimale di GET /status per il Safety Monitor.
    /// v1.2: controller.guiding_state (logica STAR_LOST). v1.4 (N6): nina.transparency
    /// (state discreto CLEAR/HAZE/CLOUD + fresh) per la safety-su-nubi.
    /// v1.5 (fix N6): TelemetryAgeS (eta' telemetria, osservabilita') e AgentReachable
    /// (false = /about irraggiungibile: il decision engine arma il watchdog agent-lost —
    /// prima l'irraggiungibilita' produceva disconnect-to-SAFE, fail-dangerous).
    /// IsValid = false quando il payload e' null/incompleto/malformato => no-op nel decision engine.
    /// I campi trasparenza sono TOLLERANTI: assenti (Agente vecchio / telemetria off) =>
    /// TransparencyState=null, TransparencyFresh=false.
    /// </summary>
    public sealed record AgentStatusSnapshot(
        string? GuidingState,
        bool IsValid,
        string? TransparencyState = null,
        bool TransparencyFresh = false,
        double? TransparencyIndex = null,
        double? TelemetryAgeS = null,
        bool AgentReachable = true,
        // §68 — osservabilita' del CANALE di guida (non la sua qualita'). Tutti
        // opzionali: un Agente <v2.9 non li espone => null/false => latch inerte.
        double? GuideFrameAgeS = null,      // eta' dell'ultimo frame QUALSIASI (guida o looping)
        double? GuideStepAgeS = null,       // eta' dell'ultimo GuideStep (da quanto non si guida)
        bool GuidingExpected = false,       // PHD2 non ha annunciato alcuna pausa
        int GuideStarErrorsRecent = 0,      // ErrorCode per-frame nella finestra recente
        bool GuideAlertSevere = false,     // Alert PHD2 warning/error recente
        // §76 — evidenza che il cielo sta PEGGIORANDO vista dal canale guida
        // (3 s) mentre N1 e' ancora fermo all'ultima posa (300 s). Assente su
        // Agenti <v2.14 => false => comportamento pre-§76.
        bool SkyDegrading = false,
        // §126 — la guida giudica il cielo. Tutti opzionali: un Agente <3.1 non li
        // espone => StarLost null => logica STAR_LOST storica sul guiding_state.
        bool? StarLost = null,              // stella persa ADESSO, misurata sugli eventi PHD2
        double? StarLostS = null,           // da quanti secondi di fila
        bool StarTracked = false,           // ultimo frame con stella, e recente
        bool SkyOk = false,                 // SNR sopra il recupero (80%), sostenuta 60 s
        bool? ChannelReady = null,          // §71 — stella tracciata in modo stabile
        // §126-bis — l'Agente DICHIARA di poter fare da giudice del cielo (3.1+,
        // sensore acceso e canale osservato). Assente o falso => giudice camera (1.13).
        bool GuideJudgeReady = false);

    /// <summary>
    /// Poller leggero che interroga GET &lt;DashboardUrl&gt;/about a intervalli regolari.
    /// Espone lo stato corrente e solleva StatusChanged SOLO sulle transizioni (non a ogni tick).
    /// Non propaga mai eccezioni: timeout/refused/JSON malformato => Offline.
    /// </summary>
    public sealed class AgentHealthChecker : IDisposable
    {
        private readonly PluginSettings _settings;
        private readonly HttpClient _http;
        private Timer? _timer;
        private AgentHealth _current = new(false, null);
        private bool _started;
        private bool _disposed;
        private volatile bool _statusPollingEnabled;

        public AgentHealth Current => _current;

        /// <summary>
        /// Quando true il tick di polling interroga anche GET /status (oltre a /about) e solleva
        /// StatusUpdated. Il Safety Monitor lo abilita in Connect() e lo disabilita in Disconnect(),
        /// così non sprechiamo banda sul payload piu' pesante quando il driver non e' connesso.
        /// </summary>
        public bool StatusPollingEnabled
        {
            get => _statusPollingEnabled;
            set => _statusPollingEnabled = value;
        }

        /// <summary>Invocato sul thread del timer SOLO quando lo stato cambia (online/offline o versione).</summary>
        public event Action<AgentHealth>? StatusChanged;

        /// <summary>
        /// Invocato sul thread del timer a OGNI tick quando StatusPollingEnabled e' true (non solo sulle
        /// transizioni: il decision engine conta i tick consecutivi, quindi deve vederli tutti).
        /// </summary>
        public event Action<AgentStatusSnapshot>? StatusUpdated;

        public AgentHealthChecker(PluginSettings settings)
        {
            _settings = settings;
            // 5s (era 3s): riduce gli "offline" transitori quando l'agente e' momentaneamente
            // lento a rispondere. Robustezza di comunicazione — NON e' il fix di N6.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _settings.IntervalChanged += OnIntervalChanged;
        }

        public void Start()
        {
            if (_started || _disposed) { return; }
            _started = true;
            var interval = TimeSpan.FromSeconds(_settings.HealthCheckIntervalSeconds);
            _timer = new Timer(OnTick, null, TimeSpan.Zero, interval);
        }

        private void OnIntervalChanged()
        {
            if (!_started || _disposed) { return; }
            var interval = TimeSpan.FromSeconds(_settings.HealthCheckIntervalSeconds);
            _timer?.Change(TimeSpan.Zero, interval);
        }

        private async void OnTick(object? state)
        {
            // async void: il corpo non deve MAI lasciar sfuggire eccezioni (crasherebbe NINA).
            // Fix N6 (Bug C-bis): try/catch PER-STADIO. Un subscriber che lancia (es. eccezione
            // cross-thread WPF) viene loggato col motivo e NON salta piu' la valutazione safety
            // del tick — prima l'intero tick veniva abbandonato in silenzio.
            AgentHealth health;
            try
            {
                health = await ProbeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ProbeAsync non lancia per design; cintura di sicurezza.
                Logger.Error($"AgentHealthChecker: probe failed unexpectedly ({ex.Message})");
                health = new AgentHealth(false, null);
            }

            try
            {
                HandleHealth(health);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AgentHealthChecker: a StatusChanged subscriber threw ({ex.Message}) — safety evaluation continues");
            }

            if (!_statusPollingEnabled) { return; }

            // /status va interrogato a OGNI tick. Fix N6 (Bug C): se l'agente e' irraggiungibile
            // il Safety Monitor deve comunque vedere il tick (AgentReachable=false) per armare
            // il watchdog agent-lost — prima il tick offline era invisibile alla safety.
            AgentStatusSnapshot snap;
            if (!health.IsOnline)
            {
                snap = new AgentStatusSnapshot(null, false, AgentReachable: false);
            }
            else
            {
                snap = await ProbeStatusAsync().ConfigureAwait(false);
            }

            try
            {
                StatusUpdated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Logger.Warning($"AgentHealthChecker: a StatusUpdated subscriber threw ({ex.Message})");
            }
        }

        /// <summary>Applica lo stato /about: aggiorna Current e solleva StatusChanged SOLO sulle transizioni.</summary>
        private void HandleHealth(AgentHealth health)
        {
            var previous = _current;
            if (health == previous) { return; } // record equality: nessun cambiamento, nessun evento, nessun log

            _current = health;
            if (health.IsOnline != previous.IsOnline)
            {
                Logger.Info(health.IsOnline
                    ? $"Adaptive Agent online v{health.Version}"
                    : "Adaptive Agent offline");
            }
            StatusChanged?.Invoke(health);
        }

        private async Task<AgentHealth> ProbeAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/about";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return new AgentHealth(false, null); }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var version = doc.RootElement.TryGetProperty("version", out var v)
                    ? v.GetString()
                    : null;
                return new AgentHealth(true, version);
            }
            catch
            {
                // ConnectionRefused / timeout / 5xx / JSON malformato => Offline. Nessun log per tick.
                return new AgentHealth(false, null);
            }
        }

        /// <summary>
        /// Legge GET /status estraendo SOLO controller.guiding_state via JsonDocument (niente DTO pesante).
        /// Errori di rete / payload malformato / campo assente => AgentStatusSnapshot(null, false).
        /// </summary>
        private async Task<AgentStatusSnapshot> ProbeStatusAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/status";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return new AgentStatusSnapshot(null, false); }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                // §49 N6 — trasparenza (nina.transparency): state discreto + fresh + index.
                // Tollerante e version-agnostic (JSON puro): campi assenti => neutri (fail-safe).
                string? transpState = null;
                bool transpFresh = false;
                double? transpIndex = null;
                double? telemetryAge = null;
                if (doc.RootElement.TryGetProperty("nina", out var nina)
                    && nina.ValueKind == JsonValueKind.Object
                    && nina.TryGetProperty("transparency", out var transp)
                    && transp.ValueKind == JsonValueKind.Object)
                {
                    if (transp.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                    {
                        transpState = st.GetString();
                    }
                    if (transp.TryGetProperty("fresh", out var fr)
                        && (fr.ValueKind == JsonValueKind.True || fr.ValueKind == JsonValueKind.False))
                    {
                        transpFresh = fr.GetBoolean();
                    }
                    if (transp.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number)
                    {
                        transpIndex = ix.GetDouble();
                    }
                    // v1.5 (fix N6 §3) — eta' della telemetria in secondi, per log/diagnosi.
                    // Tollerante: Agente <v2.8 non la espone => null.
                    if (transp.TryGetProperty("age_s", out var ag) && ag.ValueKind == JsonValueKind.Number)
                    {
                        telemetryAge = ag.GetDouble();
                    }
                }

                // §68 — blocco guide_health: osservabilita' del canale di guida.
                // Tollerante come tutto il resto: assente => valori neutri (latch inerte).
                double? guideFrameAge = null;
                double? guideStepAge = null;
                bool guidingExpected = false;
                int starErrors = 0;
                bool alertSevere = false;
                bool? starLost = null;
                double? starLostS = null;
                bool starTracked = false;
                bool? channelReady = null;
                if (doc.RootElement.TryGetProperty("guide_health", out var gh)
                    && gh.ValueKind == JsonValueKind.Object
                    && gh.TryGetProperty("enabled", out var ghEn)
                    && ghEn.ValueKind == JsonValueKind.True)
                {
                    if (gh.TryGetProperty("frame_age_s", out var fa) && fa.ValueKind == JsonValueKind.Number)
                    {
                        guideFrameAge = fa.GetDouble();
                    }
                    if (gh.TryGetProperty("guide_age_s", out var ga) && ga.ValueKind == JsonValueKind.Number)
                    {
                        guideStepAge = ga.GetDouble();
                    }
                    if (gh.TryGetProperty("guiding_expected", out var ge)
                        && (ge.ValueKind == JsonValueKind.True || ge.ValueKind == JsonValueKind.False))
                    {
                        guidingExpected = ge.GetBoolean();
                    }
                    if (gh.TryGetProperty("star_errors_recent", out var se) && se.ValueKind == JsonValueKind.Number)
                    {
                        starErrors = se.GetInt32();
                    }
                    if (gh.TryGetProperty("alert_severe", out var al)
                        && (al.ValueKind == JsonValueKind.True || al.ValueKind == JsonValueKind.False))
                    {
                        alertSevere = al.GetBoolean();
                    }
                    // §126 — stella persa sugli eventi (Agente >= 3.1).
                    if (gh.TryGetProperty("star_lost", out var sl)
                        && (sl.ValueKind == JsonValueKind.True || sl.ValueKind == JsonValueKind.False))
                    {
                        starLost = sl.GetBoolean();
                    }
                    if (gh.TryGetProperty("star_lost_s", out var sls) && sls.ValueKind == JsonValueKind.Number)
                    {
                        starLostS = sls.GetDouble();
                    }
                    if (gh.TryGetProperty("star_tracked", out var stk) && stk.ValueKind == JsonValueKind.True)
                    {
                        starTracked = true;
                    }
                    if (gh.TryGetProperty("channel_ready", out var cr))
                    {
                        if (cr.ValueKind == JsonValueKind.True) { channelReady = true; }
                        else if (cr.ValueKind == JsonValueKind.False) { channelReady = false; }
                    }
                }

                // §76 — evidenza di degrado dal canale guida (recovery_hint.degrading).
                bool skyDegrading = false;
                bool skyOk = false;
                bool guideJudgeReady = false;
                if (doc.RootElement.TryGetProperty("recovery_hint", out var rhBlock)
                    && rhBlock.ValueKind == JsonValueKind.Object)
                {
                    if (rhBlock.TryGetProperty("degrading", out var deg)
                        && deg.ValueKind == JsonValueKind.True)
                    {
                        skyDegrading = true;
                    }
                    // §126 — evidenza di sereno dalla guida (Agente 3.1). §126-bis: nessun
                    // ripiego su `active` dell'Agente 3.0, che resta falso finche' la
                    // camera dice sereno: dava l'illusione di compatibilita' e chiudeva
                    // in uno stallo. Con un Agente vecchio decide la camera (1.13).
                    if (rhBlock.TryGetProperty("sky_ok", out var ok))
                    {
                        skyOk = ok.ValueKind == JsonValueKind.True;
                    }
                    if (rhBlock.TryGetProperty("giudice_pronto", out var gp))
                    {
                        guideJudgeReady = gp.ValueKind == JsonValueKind.True;
                    }
                }

                if (doc.RootElement.TryGetProperty("controller", out var controller)
                    && controller.ValueKind == JsonValueKind.Object
                    && controller.TryGetProperty("guiding_state", out var gs)
                    && gs.ValueKind == JsonValueKind.String)
                {
                    return new AgentStatusSnapshot(gs.GetString(), true, transpState, transpFresh,
                                                   transpIndex, telemetryAge, AgentReachable: true,
                                                   GuideFrameAgeS: guideFrameAge,
                                                   GuideStepAgeS: guideStepAge,
                                                   GuidingExpected: guidingExpected,
                                                   GuideStarErrorsRecent: starErrors,
                                                   GuideAlertSevere: alertSevere,
                                                   SkyDegrading: skyDegrading,
                                                   StarLost: starLost,
                                                   StarLostS: starLostS,
                                                   StarTracked: starTracked,
                                                   SkyOk: skyOk,
                                                   ChannelReady: channelReady,
                                                   GuideJudgeReady: guideJudgeReady);
                }
                // Payload presente ma senza il campo atteso => no-op per il decision engine.
                return new AgentStatusSnapshot(null, false);
            }
            catch
            {
                return new AgentStatusSnapshot(null, false);
            }
        }

        /// <summary>Probe one-shot di /about (usato dal Safety Monitor in Connect per il check iniziale).</summary>
        public Task<AgentHealth> CheckOnceAsync() => ProbeAsync();

        /// <summary>Probe one-shot di /status (usato dal Safety Monitor in Connect per popolare lo stato iniziale).</summary>
        public Task<AgentStatusSnapshot> CheckStatusOnceAsync() => ProbeStatusAsync();

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _settings.IntervalChanged -= OnIntervalChanged;
            _timer?.Dispose();
            _timer = null;
            _http.Dispose();
        }
    }
}
