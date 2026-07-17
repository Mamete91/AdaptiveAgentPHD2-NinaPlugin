#nullable enable

namespace AdaptiveAgentForPHD2.NinaPlugin.Lifecycle
{
    /// <summary>
    /// §58 — decisioni PURE del ciclo di vita (testabili, stile RecoveryProbeGate):
    /// quando auto-avviare l'Agente e quando richiederne lo spegnimento graceful.
    ///
    /// Paletti del Gate §58:
    ///   1. Proprieta': si spegne cio' che si e' avviato (politica A). La politica B
    ///      (ManageExternalAgent) e' opt-in e adotta anche un Agente pre-esistente.
    ///   3. Opt-in: l'auto-avvio e' una scelta dell'utente (default OFF).
    /// </summary>
    public static class LifecycleGate
    {
        /// <summary>Auto-avvio all'Initialize del plugin: solo se opt-in, path configurato e Agente NON gia' raggiungibile.</summary>
        public static (bool Launch, string Reason) ShouldAutoLaunch(
            bool autoLaunchEnabled, bool pathConfigured, bool agentReachable)
        {
            if (!autoLaunchEnabled) { return (false, "auto-launch disabled (opt-in)"); }
            if (!pathConfigured) { return (false, "launcher path not configured"); }
            if (agentReachable) { return (false, "agent already running (probe)"); }
            return (true, "auto-launch: agent not reachable and path configured");
        }

        /// <summary>
        /// Spegnimento al Teardown: politica A = solo se avviato dal plugin;
        /// politica B (ManageExternalAgent) = anche un agente esterno raggiungibile.
        /// </summary>
        public static (bool Stop, string Reason) ShouldRequestShutdown(
            bool launchedByPlugin, bool manageExternalAgent, bool agentReachable)
        {
            if (launchedByPlugin)
            {
                return (true, "stopping agent launched by this plugin (policy A)");
            }
            if (manageExternalAgent && agentReachable)
            {
                return (true, "stopping external agent (policy B, opt-in)");
            }
            if (manageExternalAgent)
            {
                return (false, "policy B but no agent reachable");
            }
            return (false, "agent not launched by plugin (policy A): leaving it alone");
        }
    }
}
