#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Launch;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Lifecycle
{
    /// <summary>
    /// §58 — il plugin diventa proprietario del ciclo di vita dell'Agente:
    /// avvio automatico all'Initialize (opt-in, guarded dal probe "gia' in esecuzione")
    /// e spegnimento GRACEFUL al Teardown (POST /shutdown → restore baseline PHD2 →
    /// uscita), con fallback kill-ALBERO solo oltre il timeout.
    ///
    /// Motivazione dal campo: gli utenti chiudono l'Agente con hard-kill → uscita
    /// sporca → baseline orfana (§56) + PHD2 lasciato con le leve sintonizzate.
    /// Se start e stop li gestisce il plugin, il comportamento scorretto sparisce
    /// alla radice (il §56 resta il backstop per crash di NINA/OS).
    ///
    /// Robustezza (paletto 4): tutto asincrono e in try/catch — l'avvio di NINA non
    /// viene MAI rallentato ne' compromesso; il Teardown delega dopo il 200 (§59)
    /// e NINA si chiude all'istante; l'agente garantisce la propria terminazione.
    ///
    /// Caso "raggiungibile-ma-piantato" (02:48 del 12/7): uvicorn (thread daemon)
    /// puo' rispondere a /about anche col main-loop bloccato → /shutdown setta un
    /// evento che il loop piantato non consumera' MAI → §59: e' il WATCHDOG INTERNO
    /// dell'agente (auto-uscita ~25 s dopo il 200) a risolvere il piantato, in ogni
    /// contesto e non solo alla chiusura di NINA (poi §56 al riavvio).
    /// </summary>
    public sealed class AgentLifecycleCoordinator
    {
        private readonly PluginSettings _settings;
        private readonly AgentHealthChecker _health;
        private readonly AgentLauncher _launcher;
        private readonly HttpClient _http;

        /// <summary>Flag dell'auto-avvio riuscito (Initialize §58).</summary>
        public bool LaunchedByPlugin { get; private set; }

        /// <summary>
        /// Paletto 1 — proprieta' effettiva: l'Agente e' "nostro" se avviato dal plugin
        /// in QUALUNQUE modo — auto-avvio §58 O pulsante manuale della dashboard (stesso
        /// AgentLauncher condiviso → LastStartedProcess valorizzato). Un agente avviato
        /// fuori dal plugin non e' mai owned (politica A).
        /// </summary>
        public bool OwnsAgent => LaunchedByPlugin || _launcher.LastStartedProcess != null;

        public AgentLifecycleCoordinator(PluginSettings settings,
                                         AgentHealthChecker health,
                                         AgentLauncher launcher)
        {
            _settings = settings;
            _health = health;
            _launcher = launcher;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        // ------------------------------------------------------------------ //
        //  Avvio automatico (Initialize del plugin) — fire-and-forget         //
        // ------------------------------------------------------------------ //

        /// <summary>Da chiamare in Plugin.Initialize SENZA await (Task.Run): mai bloccare NINA.</summary>
        public async Task AutoLaunchAsync()
        {
            try
            {
                var pathConfigured = !string.IsNullOrWhiteSpace(_settings.AgentBatPath);
                var health = await _health.CheckOnceAsync().ConfigureAwait(false);
                var (launch, reason) = LifecycleGate.ShouldAutoLaunch(
                    _settings.AutoLaunchEnabled, pathConfigured, health.IsOnline);
                Logger.Info($"Agent lifecycle: auto-launch decision — {reason}");
                if (!launch)
                {
                    LaunchedByPlugin = false;   // se gia' vivo, NON l'abbiamo avviato noi
                    return;
                }

                var result = await _launcher.LaunchAsync(_settings.AgentBatPath).ConfigureAwait(false);
                if (result.Success)
                {
                    LaunchedByPlugin = true;
                    Logger.Info("Agent lifecycle: Adaptive Agent auto-launched by the plugin");
                }
                else
                {
                    Logger.Warning($"Agent lifecycle: auto-launch failed — {result.Message}");
                    ToastHelper.Show(() => Notification.ShowError(
                        string.Format(Loc.T("Toast_AutoLaunchFailed"), result.Message)));
                }
            }
            catch (Exception ex)
            {
                // Paletto 4: nessuna eccezione deve mai propagare verso l'avvio di NINA.
                Logger.Error($"Agent lifecycle: auto-launch error ({ex.Message}) — NINA startup unaffected");
                ToastHelper.Show(() => Notification.ShowError(
                    string.Format(Loc.T("Toast_AutoLaunchError"), ex.Message)));
            }
        }

        // ------------------------------------------------------------------ //
        //  Spegnimento graceful (Teardown del plugin)                         //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Da chiamare in Plugin.Teardown. §59 — DELEGA DOPO IL 200: l'agente v2.7+
        /// garantisce la terminazione (watchdog interno di auto-uscita, ~25 s) quindi il
        /// plugin NON attende piu' la scomparsa del processo — NINA si chiude subito.
        /// Il fallback kill-ALBERO resta per il caso "HTTP morto ma processo owned vivo"
        /// (POST fallito). Un agente PIANTATO col main loop bloccato risponde comunque
        /// 200 (uvicorn e' un thread daemon) e viene terminato dal SUO watchdog — meglio
        /// del vecchio timeout lato plugin, che funzionava solo alla chiusura di NINA.
        /// </summary>
        public async Task StopAgentIfOwnedAsync()
        {
            try
            {
                // Uscita immediata senza alcun probe HTTP quando la politica non puo'
                // comunque fermare nulla (ne' owned ne' gestione esterna): Teardown ~0 ms.
                if (!OwnsAgent && !_settings.ManageExternalAgent)
                {
                    Logger.Info("Agent lifecycle: shutdown decision — agent not launched by plugin (policy A): leaving it alone");
                    return;
                }

                var health = await _health.CheckOnceAsync().ConfigureAwait(false);
                var (stop, reason) = LifecycleGate.ShouldRequestShutdown(
                    OwnsAgent, _settings.ManageExternalAgent, health.IsOnline);
                Logger.Info($"Agent lifecycle: shutdown decision — {reason}");
                if (!stop) { return; }

                if (await RequestGracefulShutdownAsync().ConfigureAwait(false))
                {
                    // §59 — contratto del 200: da qui la responsabilita' e' dell'agente
                    // (graceful con restore baseline; watchdog interno se piantato).
                    Logger.Info("Agent lifecycle: shutdown accepted by the Agent — delegated "
                                + "(graceful restore in progress; Agent self-terminates if stalled). NINA can close now");
                    return;
                }

                // Fallback (paletto 2: MAI come prima scelta): il POST e' fallito (HTTP
                // morto) ma il processo owned potrebbe essere vivo → kill dell'ALBERO
                // (l'handle puo' essere cmd.exe del .bat: il python e' un figlio).
                var proc = _launcher.LastStartedProcess;
                if (proc != null && !proc.HasExited)
                {
                    Logger.Warning("Agent lifecycle: /shutdown not accepted (HTTP failed) — "
                                   + "killing owned process TREE (fallback)");
                    proc.Kill(entireProcessTree: true);
                }
                else
                {
                    Logger.Warning("Agent lifecycle: /shutdown not accepted and no owned process "
                                   + "handle available — leaving it to §56 orphan recovery at next start");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Agent lifecycle: shutdown error ({ex.Message}) — NINA close continues");
            }
        }

        /// <summary>POST /shutdown → true se l'Agente ha accettato (200).</summary>
        private async Task<bool> RequestGracefulShutdownAsync()
        {
            try
            {
                var url = _settings.DashboardUrl.TrimEnd('/') + "/shutdown";
                using var response = await _http.PostAsync(url, content: null).ConfigureAwait(false);
                Logger.Info($"Agent lifecycle: POST /shutdown -> {(int)response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Agent lifecycle: POST /shutdown failed ({ex.Message})");
                return false;
            }
        }

    }

    /// <summary>
    /// Toast marshallate sul dispatcher UI (lezione cross-thread §55). Helper locale:
    /// i file del Safety Monitor (§55) restano intoccati.
    /// </summary>
    internal static class ToastHelper
    {
        public static void Show(Action toast)
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
                Logger.Debug($"Agent lifecycle: toast failed ({ex.Message}) — ignored");
            }
        }
    }
}
