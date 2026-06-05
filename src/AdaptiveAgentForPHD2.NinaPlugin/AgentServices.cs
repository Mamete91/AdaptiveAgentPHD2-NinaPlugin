#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Launch;
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin
{
    /// <summary>
    /// Composition root statico e leggero. Condivide le istanze singleton tra il plugin
    /// (che gestisce il lifecycle del poller in Initialize/Teardown) e il DockableVM
    /// (creato separatamente da MEF). Volutamente NON usiamo l'injection MEF nel
    /// costruttore del DockableVM per non rischiare di rompere il caricamento del
    /// pannello dockable v1.0 (la firma del costruttore resta invariata: solo IProfileService).
    /// </summary>
    internal sealed class AgentServices
    {
        private static readonly Lazy<AgentServices> _instance = new(() => new AgentServices());

        public static AgentServices Instance => _instance.Value;

        public PluginSettings Settings { get; }
        public AgentLauncher Launcher { get; }
        public AgentHealthChecker HealthChecker { get; }

        // v1.2: decision engine condiviso (stesso pattern Lazy del resto del composition root).
        public Lazy<SafetyDecisionEngine> SafetyEngine { get; }
            = new(() => new SafetyDecisionEngine());

        // v1.2: istanza singleton del Safety Monitor. Creata pigramente al primo scan equipment
        // (via AdaptiveAgentSafetyMonitorProvider). Singleton => sopravvive ai rescan.
        private readonly Lazy<AdaptiveAgentSafetyMonitor> _safetyMonitor;
        public AdaptiveAgentSafetyMonitor SafetyMonitor => _safetyMonitor.Value;

        private AgentServices()
        {
            Settings = PluginSettings.Load();
            Launcher = new AgentLauncher();
            HealthChecker = new AgentHealthChecker(Settings);
            _safetyMonitor = new(() =>
                new AdaptiveAgentSafetyMonitor(Settings, HealthChecker, SafetyEngine.Value));
        }
    }
}
