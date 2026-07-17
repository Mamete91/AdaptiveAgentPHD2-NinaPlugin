#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>§60 — localizzazione del plugin (resx embedded EN/IT + indexer bindabile).</summary>
    [TestClass]
    public sealed class LocTests
    {
        [TestCleanup]
        public void Cleanup() => Loc.Instance.SetLanguage("");   // torna a Follow N.I.N.A.

        [TestMethod]
        public void English_And_Italian_ResolveFromEmbeddedResources()
        {
            Loc.Instance.SetLanguage("en");
            Assert.AreEqual("Launch Adaptive Agent", Loc.T("Dash_Launch"));
            Loc.Instance.SetLanguage("it");
            Assert.AreEqual("Avvia Adaptive Agent", Loc.T("Dash_Launch"));
        }

        [TestMethod]
        public void EveryKey_ExistsInBothLanguages_NoSilentFallback()
        {
            // Il fallback IT->EN esiste per robustezza, ma le due resx DEVONO essere
            // complete: una chiave presente solo in EN sarebbe una traduzione dimenticata.
            var rmEn = new System.Resources.ResourceManager(
                "AdaptiveAgentForPHD2.NinaPlugin.Localization.Strings_en", typeof(Loc).Assembly);
            var rmIt = new System.Resources.ResourceManager(
                "AdaptiveAgentForPHD2.NinaPlugin.Localization.Strings_it", typeof(Loc).Assembly);
            var setEn = rmEn.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!;
            var setIt = rmIt.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!;
            var keysEn = new System.Collections.Generic.HashSet<string>();
            var keysIt = new System.Collections.Generic.HashSet<string>();
            foreach (System.Collections.DictionaryEntry e in setEn) { keysEn.Add((string)e.Key); }
            foreach (System.Collections.DictionaryEntry e in setIt) { keysIt.Add((string)e.Key); }
            keysEn.SymmetricExceptWith(keysIt);
            Assert.AreEqual(0, keysEn.Count, "chiavi non allineate tra EN e IT: " + string.Join(", ", keysEn));
        }

        [TestMethod]
        public void UnknownKey_FallsBackToKeyItself_NeverEmpty()
        {
            Loc.Instance.SetLanguage("it");
            Assert.AreEqual("Chiave_Inesistente_X", Loc.T("Chiave_Inesistente_X"));
        }

        [TestMethod]
        public void FollowNina_UsesEnglish_WhenCultureIsNotItalian()
        {
            Loc.Instance.SetLanguage("");
            var saved = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                Assert.AreEqual("Launch Adaptive Agent", Loc.T("Dash_Launch"));
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo("it-IT");
                Assert.AreEqual("Avvia Adaptive Agent", Loc.T("Dash_Launch"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = saved;
            }
        }

        [TestMethod]
        public void TooltipKeys_ResolveNonEmptyAndTranslated_InBothLanguages()
        {
            // §60 (tooltip parametri N6): la simmetria EN<->IT e' gia' coperta dal test
            // generale; qui blindiamo che le 6 chiavi esistano, risolvano un testo vero
            // (non la chiave stessa, non un placeholder corto) e siano davvero TRADOTTE.
            string[] tips =
            {
                "Settings_Tip_DegradedPolls", "Settings_Tip_ClearPolls",
                "Settings_Tip_AccumBelow", "Settings_Tip_DrainAbove",
                "Settings_Tip_StalePolls", "Settings_Tip_AgentLostPolls",
            };
            foreach (var key in tips)
            {
                Loc.Instance.SetLanguage("en");
                var en = Loc.T(key);
                Loc.Instance.SetLanguage("it");
                var it = Loc.T(key);
                Assert.AreNotEqual(key, en, $"{key} non risolta in EN");
                Assert.AreNotEqual(key, it, $"{key} non risolta in IT");
                Assert.IsTrue(en.Length > 40, $"{key} sospettosamente corta in EN");
                Assert.IsTrue(it.Length > 40, $"{key} sospettosamente corta in IT");
                Assert.AreNotEqual(en, it, $"{key} identica nelle due lingue: traduzione mancante");
            }
        }

        [TestMethod]
        public void FormatStrings_KeepPlaceholders_InBothLanguages()
        {
            Loc.Instance.SetLanguage("en");
            StringAssert.Contains(Loc.T("Dash_AgentOnlineV"), "{0}");
            Loc.Instance.SetLanguage("it");
            StringAssert.Contains(Loc.T("Dash_AgentOnlineV"), "{0}");
            StringAssert.Contains(Loc.T("Toast_ProbeFailed"), "{0}");
            StringAssert.Contains(Loc.T("Launch_Error"), "{0}");
        }
    }
}
