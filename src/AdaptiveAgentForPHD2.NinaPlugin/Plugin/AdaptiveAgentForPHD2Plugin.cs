using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
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
        [ImportingConstructor]
        public AdaptiveAgentForPHD2Plugin()
        {
        }

        /// <summary>Esposto per il binding della pagina opzioni del plugin.</summary>
        public PluginSettings Settings => AgentServices.Instance.Settings;

        public override Task Initialize()
        {
            AgentServices.Instance.HealthChecker.Start();
            return Task.CompletedTask;
        }

        public override Task Teardown()
        {
            AgentServices.Instance.HealthChecker.Dispose();
            return Task.CompletedTask;
        }
    }
}
