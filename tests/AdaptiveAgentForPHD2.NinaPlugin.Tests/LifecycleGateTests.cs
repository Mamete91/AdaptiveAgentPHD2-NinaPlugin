#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Lifecycle;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §58 — decisioni pure del ciclo di vita (auto-launch + shutdown policy A/B).
    /// Casi 3/4/6/8 del prompt sul gate; l'orchestrazione async e' campo-validata.
    /// </summary>
    [TestClass]
    public sealed class LifecycleGateTests
    {
        // ---- Caso 3: auto-avvio SALTATO se l'Agente e' gia' raggiungibile ----
        [TestMethod]
        public void AutoLaunch_Skipped_WhenAgentAlreadyReachable()
        {
            var (launch, reason) = LifecycleGate.ShouldAutoLaunch(
                autoLaunchEnabled: true, pathConfigured: true, agentReachable: true);
            Assert.IsFalse(launch);
            StringAssert.Contains(reason, "already running");
        }

        // ---- Caso 4: auto-avvio eseguito se non raggiungibile + opt-in + path ----
        [TestMethod]
        public void AutoLaunch_Runs_WhenEnabledConfiguredAndNotReachable()
        {
            var (launch, _) = LifecycleGate.ShouldAutoLaunch(true, true, false);
            Assert.IsTrue(launch);
        }

        [TestMethod]
        public void AutoLaunch_Skipped_WithoutConfiguredPath()
        {
            var (launch, reason) = LifecycleGate.ShouldAutoLaunch(true, false, false);
            Assert.IsFalse(launch);
            StringAssert.Contains(reason, "path");
        }

        // ---- Caso 8: opt-in — AutoLaunchEnabled=false => nessun auto-avvio ----
        [TestMethod]
        public void AutoLaunch_Skipped_WhenDisabled_OptIn()
        {
            var (launch, reason) = LifecycleGate.ShouldAutoLaunch(false, true, false);
            Assert.IsFalse(launch);
            StringAssert.Contains(reason, "opt-in");
        }

        // ---- Caso 6: politica A — si spegne solo cio' che si e' avviato ----
        [TestMethod]
        public void Shutdown_PolicyA_OnlyOwnedAgent()
        {
            var (stop, _) = LifecycleGate.ShouldRequestShutdown(
                launchedByPlugin: true, manageExternalAgent: false, agentReachable: true);
            Assert.IsTrue(stop);

            (stop, var reason) = LifecycleGate.ShouldRequestShutdown(false, false, true);
            Assert.IsFalse(stop, "un agente esterno NON va spento con la politica A");
            StringAssert.Contains(reason, "policy A");
        }

        // ---- Politica B (opt-in): adotta e spegne anche un agente esterno ----
        [TestMethod]
        public void Shutdown_PolicyB_AdoptsExternalAgent()
        {
            var (stop, reason) = LifecycleGate.ShouldRequestShutdown(false, true, true);
            Assert.IsTrue(stop);
            StringAssert.Contains(reason, "policy B");
            // ...ma senza agente raggiungibile non c'e' nulla da spegnere.
            (stop, _) = LifecycleGate.ShouldRequestShutdown(false, true, false);
            Assert.IsFalse(stop);
        }

        // ---- Owned vince sempre (anche con B attiva e agente momentaneamente KO) ----
        [TestMethod]
        public void Shutdown_OwnedAgent_StoppedEvenIfUnreachable()
        {
            // Agente nostro ma piantato/irraggiungibile: la richiesta parte comunque
            // (il POST fallira' e scattera' il fallback kill-albero sull'handle).
            var (stop, _) = LifecycleGate.ShouldRequestShutdown(true, false, false);
            Assert.IsTrue(stop);
        }
    }
}
