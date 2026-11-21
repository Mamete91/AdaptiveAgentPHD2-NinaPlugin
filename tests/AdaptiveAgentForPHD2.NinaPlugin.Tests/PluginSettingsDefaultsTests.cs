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
            Assert.IsTrue(fresh.AgentLostUnsafeEnabled);
            Assert.AreEqual("", fresh.PluginLanguage, "default lingua = Follow N.I.N.A.");
        }

        [TestMethod]
        public void SettingsSavedBy114_WithRemovedKeys_LoadAndKeepTheUserValues()
        {
            // §128 — il settings.json del Minix (21/11/2026, plugin 1.14.3) con TUTTE le
            // chiavi del giudizio della camera tolte nel 1.15: si legge senza errori e i
            // valori regolati a mano restano (stella persa 60 s, sereno 2 poll, ...).
            const string json = @"{
  ""AgentBatPath"": ""C:\\Users\\aless\\Desktop\\IMMAGINI\\AGENTE 3.0\\Avvia.bat"",
  ""HealthCheckIntervalSeconds"": 15,
  ""DashboardUrl"": ""http://127.0.0.1:8080"",
  ""StarLostConsolidationSeconds"": 60,
  ""ForwardTelemetryToAgent"": true,
  ""CloudSafetyEnabled"": true,
  ""CloudUnsafePolls"": 8,
  ""ClearSafePolls"": 2,
  ""UseIndexCloudLogic"": true,
  ""CloudIndexAccumulateBelow"": 0.5,
  ""CloudIndexDrainAbove"": 0.5,
  ""StaleUnsafeEnabled"": true,
  ""StaleUnsafePolls"": 8,
  ""AgentLostUnsafeEnabled"": true,
  ""AgentLostUnsafePolls"": 4,
  ""AutoLaunchEnabled"": true,
  ""ManageExternalAgent"": true,
  ""GuideUnobservableEnabled"": true,
  ""GuideSilenceSeconds"": 90,
  ""GuideUnobservablePolls"": 3,
  ""SkyDegradingAccumulateEnabled"": true,
  ""SkyDegradingUnsafePolls"": 8,
  ""ProbeChannelGateEnabled"": true,
  ""ImagingCameraUnsafeEnabled"": false,
  ""MeridianProtectionEnabled"": true,
  ""MeridianLeadMinutes"": 4,
  ""PluginLanguage"": ""it""
}";
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aa_settings_114_" + System.Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(path, json);
            try
            {
                var s = PluginSettings.LoadFrom(path);
                Assert.AreEqual(@"C:\Users\aless\Desktop\IMMAGINI\AGENTE 3.0\Avvia.bat", s.AgentBatPath);
                Assert.AreEqual(60, s.StarLostConsolidationSeconds);
                Assert.AreEqual(2, s.ClearSafePolls);
                Assert.AreEqual(8, s.SkyDegradingUnsafePolls);
                Assert.AreEqual(4, s.AgentLostUnsafePolls);
                Assert.AreEqual(90, s.GuideSilenceSeconds);
                Assert.AreEqual(3, s.GuideUnobservablePolls);
                Assert.AreEqual(4, s.MeridianLeadMinutes);
                Assert.IsTrue(s.CloudSafetyEnabled);
                Assert.IsTrue(s.MeridianProtectionEnabled);
                Assert.AreEqual("it", s.PluginLanguage);
            }
            finally
            {
                System.IO.File.Delete(path);
                AdaptiveAgentForPHD2.NinaPlugin.Localization.Loc.Instance.SetLanguage("");
            }
        }
    }
}
