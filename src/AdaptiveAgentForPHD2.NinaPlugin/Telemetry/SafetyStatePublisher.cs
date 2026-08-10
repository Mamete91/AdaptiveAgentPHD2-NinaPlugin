#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Core.Utility;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Telemetry
{
    /// <summary>
    /// §73 — pubblica lo stato del Safety Monitor verso l'Agente (POST /nina/safety),
    /// che lo riespone su /status per la dashboard.
    ///
    /// PERCHE': la decisione di sicurezza vive QUI (latch, causa, finestra §72);
    /// l'Agente serve la dashboard ma non ne sa nulla. Senza questo canale lo stato
    /// del monitor resterebbe confinato ai log — e ormai il monitor e' un
    /// protagonista della sessione, non piu' una funzione interna.
    ///
    /// SOLO PRESENTAZIONE, in una direzione sola: nessuna risposta dell'Agente
    /// influenza il monitor. Graceful come il forwarder §42 — Agente offline,
    /// timeout o 5xx si ingoiano in silenzio (un solo log al primo errore: niente
    /// spam a ogni tick con l'Agente spento). Fire-and-forget: non si awaita mai
    /// dentro il tick N6, che deve restare intoccabile.
    /// </summary>
    public sealed class SafetyStatePublisher : IDisposable
    {
        private readonly PluginSettings _settings;
        private readonly HttpClient _http;
        private bool _errorLogged;
        private bool _disposed;

        public SafetyStatePublisher(PluginSettings settings)
        {
            _settings = settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        /// <summary>
        /// Pubblica lo stato corrente A OGNI TICK — e' un BATTITO, non una notifica
        /// di cambiamento.
        ///
        /// §73-ter (difetto trovato sul campo il 4/8): la prima versione deduplicava
        /// (POST solo al cambio di stato) mentre lo store dell'Agente ha una
        /// FRESCHEZZA. Le due meta' assumevano cose opposte: con stato stabile
        /// nessuno dei due parlava piu' e dopo 60 s la dashboard dichiarava
        /// UNKNOWN pur con il monitor perfettamente vivo. La freschezza e' giusta
        /// (invariante §55: nessuna notizia non e' mai "sicuro"), quindi a doversi
        /// correggere e' il publisher: chi ha una scadenza deve ricevere un battito.
        /// Costo reale: un POST su loopback misurato in ~5 ms (§69), a cadenza 15 s.
        ///
        /// `pollIntervalSeconds` viaggia nel payload: l'Agente ne DERIVA la propria
        /// finestra di freschezza (stesso principio del §43, dove la finestra si
        /// deriva dalla durata della posa) invece di indovinarla — l'intervallo di
        /// polling e' configurabile da 5 a 120 s e una soglia fissa sarebbe sbagliata
        /// per meta' dei valori possibili.
        /// </summary>
        public void Publish(string state, string? cause, string? detail,
                            bool connected, bool internalSafe, int pollIntervalSeconds)
        {
            if (_disposed) { return; }
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    state,
                    cause,
                    detail,
                    connected,
                    internal_safe = internalSafe,
                    poll_interval_s = pollIntervalSeconds,
                });
                _ = PostAsync(payload);
            }
            catch (Exception ex)
            {
                LogOnce($"serialization failed ({ex.Message})");
            }
        }

        private async Task PostAsync(string payload)
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/nina/safety";
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) { _errorLogged = false; }
            }
            catch (Exception ex)
            {
                // Agente offline/riavvio: caso NORMALE, non un guasto.
                LogOnce(ex.Message);   // il battito successivo riallinea da solo
            }
        }

        private void LogOnce(string reason)
        {
            if (_errorLogged) { return; }
            _errorLogged = true;
            Logger.Debug($"SafetyStatePublisher: publishing to the Agent failed ({reason}) "
                         + "— dashboard state only, safety decisions are unaffected");
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _http.Dispose();
        }
    }
}
