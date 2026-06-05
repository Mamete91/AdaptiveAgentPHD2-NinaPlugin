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
    /// Snapshot minimale di GET /status per il Safety Monitor v1.2.
    /// Leggiamo solo controller.guiding_state (l'unico campo rilevante per la logica unsafe).
    /// IsValid = false quando il payload e' null/incompleto/malformato => no-op nel decision engine.
    /// </summary>
    public sealed record AgentStatusSnapshot(string? GuidingState, bool IsValid);

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
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
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
            try
            {
                var health = await ProbeAsync().ConfigureAwait(false);
                HandleHealth(health);

                // /status va interrogato a OGNI tick (non gated dalla equality di /about):
                // il Safety Monitor lo abilita solo quando connesso.
                if (_statusPollingEnabled)
                {
                    var snap = await ProbeStatusAsync().ConfigureAwait(false);
                    StatusUpdated?.Invoke(snap);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AgentHealthChecker tick fallito: {ex.Message}");
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
                if (doc.RootElement.TryGetProperty("controller", out var controller)
                    && controller.ValueKind == JsonValueKind.Object
                    && controller.TryGetProperty("guiding_state", out var gs)
                    && gs.ValueKind == JsonValueKind.String)
                {
                    return new AgentStatusSnapshot(gs.GetString(), true);
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
