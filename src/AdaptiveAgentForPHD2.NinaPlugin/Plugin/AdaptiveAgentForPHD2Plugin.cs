using AdaptiveAgentForPHD2.NinaPlugin.Lifecycle;
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using AdaptiveAgentForPHD2.NinaPlugin.Telemetry;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Plugin
{
    /// <summary>
    /// Entry point del plugin. PluginBase legge i metadati da AssemblyInfo.cs
    /// e popola automaticamente le property di IPluginManifest.
    /// La property Settings e' il DataContext della pagina opzioni: NINA risolve il
    /// DataTemplate con chiave "&lt;AssemblyTitle&gt;_Options" e ne imposta come DataContext
    /// questa istanza di plugin (verificato in pre-flight su PluginOptionsDataTemplateSelector).
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class AdaptiveAgentForPHD2Plugin : PluginBase
    {
        // §42 — forwarder telemetria NINA->Agente. Posseduto dal plugin (non da
        // AgentServices) perche' richiede IImageSaveMediator, iniettato qui via MEF.
        private readonly AgentTelemetryForwarder _telemetryForwarder;
        // §57-bis — memoria dell'ultimo LIGHT (parametri replicati dalla RecoveryProbe).
        // Subscriber separato: il forwarder §42 resta invariato (responsabilita' distinte).
        private readonly LastLightTracker _lastLightTracker;
        // §58 — proprieta' del ciclo di vita dell'Agente: auto-avvio all'Initialize
        // (opt-in) + spegnimento graceful al Teardown (POST /shutdown → restore baseline).
        private readonly AgentLifecycleCoordinator _lifecycle;

        [ImportingConstructor]
        public AdaptiveAgentForPHD2Plugin(IImageSaveMediator imageSaveMediator,
                                          NINA.Equipment.Interfaces.Mediator.ITelescopeMediator telescopeMediator,
                                          NINA.Profile.Interfaces.IProfileService profileService)
        {
            var services = AgentServices.Instance;
            // §72 — la protezione meridiano ha bisogno di leggere la montatura (side of
            // pier, angolo orario, tracking) e le impostazioni MF del profilo.
            services.AttachNinaServices(telescopeMediator, profileService);
            _telemetryForwarder = new AgentTelemetryForwarder(imageSaveMediator, services.Settings);
            _lastLightTracker = new LastLightTracker(imageSaveMediator);
            _lifecycle = new AgentLifecycleCoordinator(
                services.Settings, services.HealthChecker, services.Launcher);
        }

        /// <summary>Esposto per il binding della pagina opzioni del plugin.</summary>
        public PluginSettings Settings => AgentServices.Instance.Settings;

        public override Task Initialize()
        {
            AgentServices.Instance.HealthChecker.Start();
            _telemetryForwarder.Subscribe();   // §42: iscrizione a ImageSaved
            _lastLightTracker.Subscribe();     // §57-bis: profilo ultimo light
            // §58 — auto-avvio FIRE-AND-FORGET (paletto 4): il probe "gia' in esecuzione"
            // e l'eventuale lancio avvengono su un task separato; l'avvio di NINA non
            // attende e non puo' essere compromesso (tutte le eccezioni sono confinate).
            _ = Task.Run(_lifecycle.AutoLaunchAsync);
            return Task.CompletedTask;
        }

        public override async Task Teardown()
        {
            // §58/§59 — spegnimento graceful PRIMA di smontare health checker e forwarder
            // (servono per il probe e il POST /shutdown). §59: dopo il 200 si DELEGA
            // all'agente (watchdog interno) — NINA si chiude subito; kill-albero solo
            // come fallback se il POST fallisce con processo owned vivo.
            await _lifecycle.StopAgentIfOwnedAsync().ConfigureAwait(false);

            _lastLightTracker.Dispose();       // §57-bis: disiscrizione simmetrica
            _telemetryForwarder.Dispose();     // §42: disiscrizione simmetrica + HttpClient
            AgentServices.Instance.HealthChecker.Dispose();
        }
    }
}
