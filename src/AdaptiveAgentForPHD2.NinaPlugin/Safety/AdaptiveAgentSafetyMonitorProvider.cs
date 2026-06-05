#nullable enable
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace AdaptiveAgentForPHD2.NinaPlugin.Safety
{
    /// <summary>
    /// Entry point MEF del Safety Monitor. Pre-flight: NINA NON importa ISafetyMonitor direttamente;
    /// il PluginLoader fa [ImportMany(typeof(IEquipmentProvider))] e il PluginEquipmentProviderManager
    /// instrada ogni provider verso il IEquipmentProviders&lt;T&gt; giusto in base all'argomento generico.
    /// Il SafetyMonitorChooserVM poi chiama GetEquipment() per popolare la tendina.
    /// Quindi il contratto corretto e' [Export(typeof(IEquipmentProvider))] su un
    /// IEquipmentProvider&lt;ISafetyMonitor&gt; (non [Export(typeof(ISafetyMonitor))]).
    ///
    /// Ritorniamo l'istanza singleton dal composition root: lo stesso oggetto sopravvive ai rescan,
    /// così un eventuale rescan mentre il driver e' connesso non lo sostituisce con un'istanza nuova.
    /// </summary>
    [Export(typeof(IEquipmentProvider))]
    public class AdaptiveAgentSafetyMonitorProvider : IEquipmentProvider<ISafetyMonitor>
    {
        [ImportingConstructor]
        public AdaptiveAgentSafetyMonitorProvider()
        {
        }

        public string Name => "Adaptive Agent for PHD2";

        public IList<ISafetyMonitor> GetEquipment()
            => new List<ISafetyMonitor> { AgentServices.Instance.SafetyMonitor };
    }
}
