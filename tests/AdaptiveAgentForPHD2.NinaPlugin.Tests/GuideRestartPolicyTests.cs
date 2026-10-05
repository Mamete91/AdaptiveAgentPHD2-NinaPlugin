#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §126-quater/sexies — quando la sonda di guida fa ripartire la guida durante un UNSAFE.
    /// Valori della sequenza reale dell'utente (NGC 7635): intervallo minimo 5 min, cadenza 12.
    /// </summary>
    [TestClass]
    public sealed class GuideRestartPolicyTests
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(12);

        private static (string? Reason, bool StopFirst) Decide(
            bool? expected, bool? lost = false, double? lostS = 0, bool? confirming = null,
            double notExpectedMin = 0, double sinceRestartMin = 0, bool minElapsed = true)
            => GuideRestartPolicy.Decide(expected, lost, lostS, confirming,
                                         TimeSpan.FromMinutes(notExpectedMin),
                                         TimeSpan.FromMinutes(sinceRestartMin),
                                         minElapsed, MinInterval, Cadence);

        [TestMethod]
        public void GuidingStopped_RestartsAfterMinInterval()
        {
            Assert.IsNull(Decide(false, notExpectedMin: 2).Reason);
            var (reason, stop) = Decide(false, notExpectedMin: 6);
            Assert.AreEqual(GuideRestartPolicy.ReasonNotRunning, reason);
            Assert.IsFalse(stop);
        }

        [TestMethod]
        public void StarLost_StopAndStart()
        {
            Assert.IsNull(Decide(true, lost: true, lostS: 120).Reason);
            var (reason, stop) = Decide(true, lost: true, lostS: 400);
            StringAssert.Contains(reason, "lost for 400 s");
            Assert.IsTrue(stop);
        }

        [TestMethod]
        public void Cadence_RestartsTheChannel()
        {
            Assert.IsNull(Decide(true, sinceRestartMin: 11).Reason);
            var (reason, stop) = Decide(true, sinceRestartMin: 13);
            StringAssert.Contains(reason, "still unsafe after 12 min");
            Assert.IsTrue(stop);
        }

        [TestMethod]
        public void Cadence_PostponedWhileTheGuideConfirmsAClearSky()
        {
            // §126-sexies — una ripartenza azzererebbe la conferma (5 min dopo un cambio in
            // UNSAFE): con una cadenza piu' corta il SAFE non arriverebbe mai.
            Assert.IsNull(Decide(true, confirming: true, sinceRestartMin: 13).Reason);
            Assert.IsNotNull(Decide(true, confirming: false, sinceRestartMin: 13).Reason);
        }

        [TestMethod]
        public void Cadence_OlderAgentWithoutTheField_BehavesLike114()
        {
            Assert.IsNotNull(Decide(true, confirming: null, sinceRestartMin: 13).Reason);
        }

        [TestMethod]
        public void StarLost_NotPostponedByConfirmation()
        {
            // Con la stella persa la guida non sta confermando niente: il riavvio resta.
            Assert.IsNotNull(Decide(true, lost: true, lostS: 400, confirming: true).Reason);
        }

        [TestMethod]
        public void NeverMoreOftenThanTheMinimumInterval()
        {
            Assert.IsNull(Decide(false, notExpectedMin: 30, minElapsed: false).Reason);
            Assert.IsNull(Decide(true, lost: true, lostS: 900, minElapsed: false).Reason);
            Assert.IsNull(Decide(true, sinceRestartMin: 60, minElapsed: false).Reason);
        }

        [TestMethod]
        public void UnknownStatus_DoesNothing()
        {
            Assert.IsNull(Decide(null, lost: null, lostS: null, sinceRestartMin: 60).Reason);
        }
    }
}
