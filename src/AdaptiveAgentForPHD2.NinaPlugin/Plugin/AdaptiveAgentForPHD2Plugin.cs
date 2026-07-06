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

        [ImportingConstructor]
        public AdaptiveAgentForPHD2Plugin(IImageSaveMediator imageSaveMediator)
        {
            _telemetryForwarder = new AgentTelemetryForwarder(
                imageSaveMediator, AgentServices.Instance.Settings);
        }

        /// <summary>Esposto per il binding della pagina opzioni del plugin.</summary>
        public PluginSettings Settings => AgentServices.Instance.Settings;

        public override Task Initialize()
        {
            AgentServices.Instance.HealthChecker.Start();
            _telemetryForwarder.Subscribe();   // §42: iscrizione a ImageSaved
            return Task.CompletedTask;
        }

        public override Task Teardown()
        {
            _telemetryForwarder.Dispose();     // §42: disiscrizione simmetrica + HttpClient
            AgentServices.Instance.HealthChecker.Dispose();
            return Task.CompletedTask;
        }
    }
}
