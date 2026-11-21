#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §128 — le sequenze salvate con la Recovery probe fino al 1.14 contengono i campi
    /// della posa-sonda (AutoTimeout, FallbackExposureSeconds), tolti nel 1.15. NINA legge
    /// le sequenze con Newtonsoft e impostazioni standard (SequenceJsonConverter.CreateSettings:
    /// solo Converters e Context), quindi MissingMemberHandling = Ignore: i campi sconosciuti
    /// si scartano e i valori dell'utente restano. Il 3/10 una Recovery probe diventata
    /// "sconosciuta" ha tenuto la notte ferma tre ore: questo test fissa che non succede.
    /// </summary>
    [TestClass]
    public sealed class RecoveryProbeCompatibilityTests
    {
        // Frammento VERO della sequenza "LRGB - HOO RC8 CON PANNELLO E FLAT - FIX.jsonNGC 7635"
        // (cartella Targets del Minix, salvata il 05/10/2026 con il plugin 1.14).
        private const string FrammentoNgc7635 = @"{
  ""$type"": ""AdaptiveAgentForPHD2.NinaPlugin.Sequencer.RecoveryProbe, AdaptiveAgentForPHD2.NinaPlugin"",
  ""AutoTimeout"": true,
  ""TimeoutMinutes"": 12.0,
  ""MinIntervalMinutes"": 5.0,
  ""FallbackExposureSeconds"": 60.0,
  ""ErrorBehavior"": 0,
  ""Attempts"": 1
}";

        [TestMethod]
        public void SequenceSavedBy114_LoadsIntoTheGuideOnlyProbe()
        {
            var probe = new RecoveryProbe(null!, null!, null!);
            JsonConvert.PopulateObject(FrammentoNgc7635, probe, new JsonSerializerSettings());
            Assert.AreEqual(12.0, probe.TimeoutMinutes);
            Assert.AreEqual(5.0, probe.MinIntervalMinutes);
        }

        [TestMethod]
        public void SavedProbe_HasNoRemovedFields()
        {
            var probe = new RecoveryProbe(null!, null!, null!) { TimeoutMinutes = 15, MinIntervalMinutes = 4 };
            var json = JsonConvert.SerializeObject(probe);
            StringAssert.Contains(json, "\"TimeoutMinutes\":15.0");
            StringAssert.Contains(json, "\"MinIntervalMinutes\":4.0");
            Assert.IsFalse(json.Contains("AutoTimeout"), json);
            Assert.IsFalse(json.Contains("FallbackExposureSeconds"), json);
        }
    }
}
