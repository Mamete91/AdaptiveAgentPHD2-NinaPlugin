#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §72 — MERIDIAN_PROTECTION: la finestra limitata che evita il deadlock del
    /// flip sotto unsafe (19/7: "Stopping tracking instead" e notte finita).
    /// Macchina a stati PURA: qui si verificano aperture, chiusure, tetto, lockout
    /// e la riattivazione ex-post del tracking — senza NINA.
    /// </summary>
    [TestClass]
    public sealed class MeridianProtectionEngineTests
    {
        private const double Lead = 4.0;

        private static (MeridianEvent, bool) T(MeridianProtectionEngine e, double now,
            bool enabled = true, bool internalSafe = false, bool mount = true,
            bool tracking = true, bool? pierWest = true, double? toDeadline = 2.0)
            => e.Tick(enabled, internalSafe, mount, tracking, pierWest, toDeadline, Lead, now);

        // ---- Apertura: solo col quadro completo ----

        [TestMethod]
        public void Opens_OnlyWithin_Lead_AndUnsafe_AndFlipPending()
        {
            var e = new MeridianProtectionEngine();

            Assert.AreEqual(MeridianEvent.None, T(e, 0, toDeadline: 30.0).Item1,
                "deadline lontana: chiusa");
            Assert.AreEqual(MeridianEvent.None, T(e, 0, internalSafe: true).Item1,
                "monitor SAFE: la finestra non serve a nulla");
            Assert.AreEqual(MeridianEvent.None, T(e, 0, pierWest: false).Item1,
                "pier gia' a est: nessun flip pendente");
            Assert.AreEqual(MeridianEvent.None, T(e, 0, pierWest: null).Item1,
                "pier ignoto: fail-inert");
            Assert.AreEqual(MeridianEvent.None, T(e, 0, mount: false).Item1,
                "montatura non connessa: fail-inert");
            Assert.AreEqual(MeridianEvent.None, T(e, 0, enabled: false).Item1,
                "kill-switch");

            var (evt, resume) = T(e, 0, toDeadline: 3.5);
            Assert.AreEqual(MeridianEvent.Opened, evt, "dentro il lead: APRE");
            Assert.IsFalse(resume, "tracking gia' attivo: nessuna azione hardware");
            Assert.IsTrue(e.WindowOpen);
        }

        // ---- Il caso della notte 19/7: flip fatto, finestra chiusa, unsafe onesto ----

        [TestMethod]
        public void FlipDone_ClosesWindow_Immediately()
        {
            var e = new MeridianProtectionEngine();
            T(e, 0, toDeadline: 2.0);
            Assert.IsTrue(e.WindowOpen);

            // qualche tick col flip in corso (pier ancora west)
            Assert.AreEqual(MeridianEvent.None, T(e, 15).Item1);
            Assert.AreEqual(MeridianEvent.None, T(e, 30, toDeadline: -1.0).Item1);

            // pier cambiato: missione compiuta, la menzogna finisce SUBITO
            var (evt, _) = T(e, 120, pierWest: false, toDeadline: -2.0);
            Assert.AreEqual(MeridianEvent.ClosedFlipDone, evt);
            Assert.IsFalse(e.WindowOpen, "l'unsafe onesto torna a ri-parcheggiare");
        }

        // ---- Ex-post: NINA ha gia' fermato il tracking (il deadlock del 19/7) ----

        [TestMethod]
        public void TrackingAlreadyStopped_IsResumed_OncePerWindow()
        {
            var e = new MeridianProtectionEngine();
            // finestra nata in ritardo: deadline oltrepassata, tracking gia' fermo
            var (evt, resume) = T(e, 0, tracking: false, toDeadline: -3.0);
            Assert.AreEqual(MeridianEvent.Opened, evt);
            Assert.IsTrue(resume, "il tracking VA riattivato dentro la finestra");

            // il comando non si ripete (una sola azione hardware per finestra)
            Assert.IsFalse(T(e, 15, tracking: false).Item2);
            Assert.IsFalse(T(e, 30, tracking: true).Item2);
        }

        // ---- Tetto: la menzogna DEVE finire; lockout senza oscillazioni ----

        [TestMethod]
        public void Timeout_ClosesToLockout_NoReopen_UntilPierChanges()
        {
            var e = new MeridianProtectionEngine();
            T(e, 0, toDeadline: 1.0);
            Assert.IsTrue(e.WindowOpen);

            double limit = MeridianProtectionEngine.WindowMaxMinutes * 60.0;
            Assert.AreEqual(MeridianEvent.None, T(e, limit - 1).Item1);
            Assert.AreEqual(MeridianEvent.ClosedTimeout, T(e, limit + 1).Item1,
                "flip mai arrivato (trigger assente?): la finestra scade");
            Assert.IsFalse(e.WindowOpen);

            // LOCKOUT: le stesse condizioni NON riaprono (mai safe/unsafe ciclici)
            Assert.AreEqual(MeridianEvent.None, T(e, limit + 60, toDeadline: -5.0).Item1);
            Assert.IsFalse(e.WindowOpen);

            // l'operatore flippa a mano -> pier east -> lockout rientra
            T(e, limit + 120, pierWest: false);
            // ...e a un NUOVO flip pendente (notte successiva) la finestra riapre
            var (evt, _) = T(e, limit + 180, pierWest: true, toDeadline: 2.0);
            Assert.AreEqual(MeridianEvent.Opened, evt);
        }

        // ---- Rientro reale in safe: la finestra diventa inutile e si chiude ----

        [TestMethod]
        public void RealSafe_ClosesWindow_Silently()
        {
            var e = new MeridianProtectionEngine();
            T(e, 0, toDeadline: 2.0);
            var (evt, _) = T(e, 30, internalSafe: true);
            Assert.AreEqual(MeridianEvent.ClosedConditionsLost, evt);
            Assert.IsFalse(e.WindowOpen, "col safe reale decide il flusso normale di NINA");
        }

        [TestMethod]
        public void MountLost_MidWindow_FailsInert()
        {
            var e = new MeridianProtectionEngine();
            T(e, 0, toDeadline: 2.0);
            var (evt, _) = T(e, 30, mount: false, pierWest: null);
            Assert.AreEqual(MeridianEvent.ClosedConditionsLost, evt);
            Assert.IsFalse(e.WindowOpen);
        }

        [TestMethod]
        public void Reset_ReturnsToIdle()
        {
            var e = new MeridianProtectionEngine();
            T(e, 0, toDeadline: 2.0);
            Assert.IsTrue(e.WindowOpen);
            e.Reset();
            Assert.IsFalse(e.WindowOpen);
        }
    
        // ---- §72-bis: la notte 20-21/11 ----

        private static (MeridianEvent, bool) F(MeridianProtectionEngine e, double now,
            bool flip, int done, bool internalSafe = false, bool tracking = true,
            bool? pierWest = true, double? toDeadline = 2.0, bool mount = true)
            => e.Tick(true, internalSafe, mount, tracking, pierWest, toDeadline, Lead, now, flip, done);

        [TestMethod]
        public void Night_2011_PierChangeDuringNinaFlip_KeepsWindowOpenUntilFlipEnds()
        {
            var e = new MeridianProtectionEngine();
            Assert.AreEqual(MeridianEvent.Opened, F(e, 0, flip: false, done: 0, toDeadline: 3.9).Item1);
            // 00:33:27 NINA avvia il flip; 00:33:29 ferma il tracking per passare il meridiano
            var (evt, resume) = F(e, 345, flip: true, done: 0, tracking: false, toDeadline: -1.7);
            Assert.AreEqual(MeridianEvent.None, evt);
            Assert.IsFalse(resume, "durante il flip il tracking lo gestisce NINA");
            // 00:34:01 il pilastro cambia: prima chiudeva qui, ora resta aperta
            Assert.AreEqual(MeridianEvent.None, F(e, 375, flip: true, done: 0, pierWest: false).Item1);
            Assert.IsTrue(e.WindowOpen, "ricentraggio e guida ancora da fare");
            Assert.AreEqual(MeridianEvent.None, F(e, 435, flip: true, done: 0, pierWest: null).Item1,
                "pilastro ignoto durante lo slew: si aspetta NINA");
            Assert.IsTrue(e.WindowOpen);
            // NINA conclude il flip
            Assert.AreEqual(MeridianEvent.ClosedFlipDone, F(e, 450, flip: false, done: 1, pierWest: false).Item1);
            Assert.IsFalse(e.WindowOpen, "l'unsafe onesto torna a flip concluso");
        }

        [TestMethod]
        public void UnsafeArrivingDuringNinaFlip_OpensWindow_WithoutTouchingTracking()
        {
            var e = new MeridianProtectionEngine();
            Assert.AreEqual(MeridianEvent.None, F(e, 0, flip: true, done: 0, internalSafe: true).Item1);
            var (evt, resume) = F(e, 15, flip: true, done: 0, tracking: false, toDeadline: 10.0);
            Assert.AreEqual(MeridianEvent.OpenedDuringFlip, evt);
            Assert.IsFalse(resume);
            Assert.AreEqual(MeridianEvent.ClosedFlipDone, F(e, 120, flip: false, done: 1, pierWest: false).Item1);
        }

        [TestMethod]
        public void FlipForcedAfterLockout_IsProtected()
        {
            var e = new MeridianProtectionEngine();
            F(e, 0, flip: false, done: 0);
            Assert.AreEqual(MeridianEvent.ClosedTimeout,
                F(e, MeridianProtectionEngine.WindowMaxMinutes * 60, flip: false, done: 0, toDeadline: -15).Item1);
            Assert.IsFalse(e.WindowOpen);
            Assert.AreEqual(MeridianEvent.OpenedDuringFlip,
                F(e, 1300, flip: true, done: 0, toDeadline: -18).Item1, "flip forzato dall'operatore");
            Assert.IsTrue(e.WindowOpen);
        }

        [TestMethod]
        public void HungNinaFlip_HasACeiling()
        {
            var e = new MeridianProtectionEngine();
            F(e, 0, flip: false, done: 0);
            F(e, 10, flip: true, done: 0);
            Assert.AreEqual(MeridianEvent.None,
                F(e, 10 + MeridianProtectionEngine.FlipMaxMinutes * 60 - 1, flip: true, done: 0).Item1);
            Assert.AreEqual(MeridianEvent.ClosedTimeout,
                F(e, 10 + MeridianProtectionEngine.FlipMaxMinutes * 60, flip: true, done: 0).Item1);
            Assert.IsFalse(e.WindowOpen, "NINA bloccata: l'unsafe onesto deve tornare");
        }

        [TestMethod]
        public void WithoutNinaNotifications_PierChangeStillCloses()
        {
            var e = new MeridianProtectionEngine();
            F(e, 0, flip: false, done: 0);
            Assert.AreEqual(MeridianEvent.ClosedFlipDone, F(e, 30, flip: false, done: 0, pierWest: false).Item1,
                "flip a mano dalla montatura: regola di prima");
        }
}
}
