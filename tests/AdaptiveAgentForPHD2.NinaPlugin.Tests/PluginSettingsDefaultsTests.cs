#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §60 — "installa e funziona": in una NUOVA installazione non esiste settings.json,
    /// quindi Load() restituisce esattamente un PluginSettings appena costruito (field
    /// initializer). Verificare i default sul costruttore equivale a verificarli sulla
    /// prima installazione. NB: SOLO letture — i setter salverebbero su disco.
    /// </summary>
    [TestClass]
    public sealed class PluginSettingsDefaultsTests
    {
        [TestMethod]
        public void NewInstallation_AgentLifecycle_IsEnabledByDefault()
        {
            var fresh = new PluginSettings();
            Assert.IsTrue(fresh.AutoLaunchEnabled,
                "auto-launch deve essere attivo su installazione nuova (§60)");
            Assert.IsTrue(fresh.ManageExternalAgent,
                "manage-external deve essere attivo su installazione nuova (§60)");
        }

        [TestMethod]
        public void LifecycleDefaults_ConstantsAgreeWithInstance()
        {
            Assert.IsTrue(PluginSettings.DefaultAutoLaunchEnabled);
            Assert.IsTrue(PluginSettings.DefaultManageExternalAgent);
        }

        [TestMethod]
        public void OtherBornOperativeDefaults_Unchanged()
        {
            var fresh = new PluginSettings();
            Assert.IsTrue(fresh.ForwardTelemetryToAgent);
            Assert.IsTrue(fresh.CloudSafetyEnabled);
            Assert.IsTrue(fresh.UseIndexCloudLogic);
            Assert.IsTrue(fresh.StaleUnsafeEnabled);
            Assert.IsTrue(fresh.AgentLostUnsafeEnabled);
            Assert.AreEqual("", fresh.PluginLanguage, "default lingua = Follow N.I.N.A.");
        }

        [TestMethod]
        public void NewInstallation_GuideChannelJudgesTheSky()
        {
            // §126 — decisione del 04/10/2026: la camera di ripresa e' informativa.
            Assert.IsFalse(new PluginSettings().ImagingCameraUnsafeEnabled);
            Assert.IsFalse(PluginSettings.DefaultImagingCameraUnsafeEnabled);
        }
    }
}
