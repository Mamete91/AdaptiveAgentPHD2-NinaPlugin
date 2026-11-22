#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §129 — quando "Recenter if needed" deve controllare l'inquadratura al ritorno del sereno.
    /// Regola dell'operatore (21/11/2026): dopo un flip passato in unsafe, oppure dopo almeno N
    /// minuti senza guida durante l'unsafe (di fabbrica 12). Tick ogni 15 s come sul Minix.
    /// </summary>
    [TestClass]
    public sealed class RecenterTrackerTests
    {
        private const double Tick = 15;
        private const double MaxStep = 45;

        private static AgentStatusSnapshot Guida(bool? lost = false, bool expected = true,
                                                 string state = "NORMAL", bool reachable = true, bool valid = true)
            => new(state, valid, AgentReachable: reachable, GuidingExpected: expected, StarLost: lost);

        /// <summary>Simula `minutes` minuti di tick con lo stesso stato.</summary>
        private static double Run(RecenterTracker t, double from, double minutes, bool internalSafe, bool? unguided)
        {
            double now = from;
            int n = (int)(minutes * 60 / Tick);
            for (int i = 0; i < n; i++)
            {
                now += Tick;
                t.OnTick(internalSafe, unguided, now, MaxStep);
            }
            return now;
        }

        private static RecenterTracker Connesso()
        {
            var t = new RecenterTracker();
            t.Reset(monitorConnected: true);
            t.OnTick(true, false, 0, MaxStep);
            return t;
        }

        [TestMethod]
        public void NubeConLaGuidaCheCorregge_NessunControllo()
        {
            // 40 minuti di unsafe con velatura, ma la stella resta agganciata: niente deriva.
            var t = Connesso();
            var now = Run(t, 0, 40, internalSafe: false, unguided: false);
            Run(t, now, 1, internalSafe: true, unguided: false);
            var d = t.Decide(12);
            Assert.IsFalse(d.Check, d.Reason);
            Assert.AreEqual(0.0, d.UnguidedMinutes, 0.01);
        }

        [TestMethod]
        public void StellaPersaQuindiciMinuti_ControlloAlRitornoDelSereno()
        {
            var t = Connesso();
            var now = Run(t, 0, 3, internalSafe: false, unguided: false);
            now = Run(t, now, 15, internalSafe: false, unguided: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            var d = t.Decide(12);
            Assert.IsTrue(d.Check, d.Reason);
            Assert.IsFalse(d.FlipDuringUnsafe);
            Assert.AreEqual(15.0, d.UnguidedMinutes, 0.3);
        }

        [TestMethod]
        public void StellaPersaDieciMinuti_SottoSoglia_NessunControllo()
        {
            var t = Connesso();
            var now = Run(t, 0, 10, internalSafe: false, unguided: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            var d = t.Decide(12);
            Assert.IsFalse(d.Check, d.Reason);
            Assert.AreEqual(10.0, d.UnguidedMinutes, 0.3);
        }

        [TestMethod]
        public void FlipSottoLeNubi_ControlloSempre_AncheSenzaMinutiSenzaGuida()
        {
            var t = Connesso();
            var now = Run(t, 0, 2, internalSafe: false, unguided: false);
            t.OnFlipStarted(internalSafe: false, target: null);
            t.OnFlipFinished(internalSafe: false);
            Run(t, now, 1, internalSafe: true, unguided: false);
            var d = t.Decide(12);
            Assert.IsTrue(d.Check, d.Reason);
            Assert.IsTrue(d.FlipDuringUnsafe);
        }

        [TestMethod]
        public void FlipACieloSereno_NessunControllo()
        {
            // Il ricentraggio di NINA a cielo sereno funziona: non si aggiunge niente.
            var t = Connesso();
            t.OnFlipStarted(internalSafe: true, target: null);
            t.OnFlipFinished(internalSafe: true);
            Run(t, 0, 1, internalSafe: true, unguided: false);
            Assert.IsFalse(t.Decide(12).Check);
        }

        [TestMethod]
        public void UnsafeArrivatoAFlipIniziato_Controllo()
        {
            var t = Connesso();
            t.OnFlipStarted(internalSafe: true, target: null);
            t.OnFlipFinished(internalSafe: false);
            Run(t, 0, 1, internalSafe: true, unguided: false);
            Assert.IsTrue(t.Decide(12).Check);
        }

        [TestMethod]
        public void FinestraMeridiano_IlMonitorRiportaSafeMaNonLoE_NessunControlloENienteConsumato()
        {
            // Dentro la finestra il Trigger On Unsafe vede SAFE e arriva in After Waiting For
            // Safety: il plate solve fallirebbe sotto le nubi e il flip sta per partire.
            var t = Connesso();
            var now = Run(t, 0, 20, internalSafe: false, unguided: true);
            var d = t.Decide(12);
            Assert.IsFalse(d.Check, d.Reason);
            // Il flip passa, poi torna il sereno: ora si controlla, con i minuti accumulati.
            t.OnFlipStarted(internalSafe: false, target: null);
            t.OnFlipFinished(internalSafe: false);
            now = Run(t, now, 30, internalSafe: false, unguided: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            d = t.Decide(12);
            Assert.IsTrue(d.Check, d.Reason);
            Assert.IsTrue(d.FlipDuringUnsafe);
            Assert.AreEqual(50.0, d.UnguidedMinutes, 0.5);
        }

        [TestMethod]
        public void DueEpisodiBrevi_SiSommano_FinoAlControlloRiuscito()
        {
            // La deriva non si azzera tra un episodio e l'altro: 7 + 7 minuti senza guida.
            var t = Connesso();
            var now = Run(t, 0, 7, internalSafe: false, unguided: true);
            now = Run(t, now, 20, internalSafe: true, unguided: false);
            Assert.IsFalse(t.Decide(12).Check);
            now = Run(t, now, 7, internalSafe: false, unguided: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            Assert.IsTrue(t.Decide(12).Check);
            t.Consume();
            var d = t.Decide(12);
            Assert.IsFalse(d.Check);
            Assert.AreEqual(0.0, d.UnguidedMinutes, 0.01);
        }

        [TestMethod]
        public void GuidaFermaACieloSereno_NonConta()
        {
            // Pause volute a sereno (cambio oggetto, autofocus): non sono deriva in unsafe.
            var t = Connesso();
            Run(t, 0, 60, internalSafe: true, unguided: true);
            Assert.AreEqual(0.0, t.Decide(12).UnguidedMinutes, 0.01);
        }

        [TestMethod]
        public void SaltoLungoFraDueTick_ContaAlMassimoIlTettoDelPasso()
        {
            var t = Connesso();
            t.OnTick(false, true, 15, MaxStep);           // primo intervallo: 15 s
            t.OnTick(false, true, 15 + 3600, MaxStep);    // PC sospeso un'ora: al massimo 45 s
            Assert.AreEqual((Tick + MaxStep) / 60.0, t.Decide(12).UnguidedMinutes, 0.01);
        }

        [TestMethod]
        public void MonitorScollegato_NessunControllo()
        {
            var t = new RecenterTracker();
            t.OnFlipStarted(false, null);
            Assert.IsFalse(t.Decide(12).Check);
            t.Reset(monitorConnected: false);
            Assert.IsFalse(t.Decide(12).Check);
        }

        [TestMethod]
        public void Ricollegamento_RipartedaZero()
        {
            var t = Connesso();
            var now = Run(t, 0, 20, internalSafe: false, unguided: true);
            t.Reset(monitorConnected: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            Assert.IsFalse(t.Decide(12).Check);
        }

        [TestMethod]
        public void SenzaGuida_SignificaStellaPersaGuidaFermaOAgenteMuto()
        {
            Assert.AreEqual(false, RecenterTracker.IsUnguided(Guida()));
            Assert.AreEqual(true, RecenterTracker.IsUnguided(Guida(lost: true)));
            Assert.AreEqual(true, RecenterTracker.IsUnguided(Guida(expected: false)), "guida ferma (anche durante il flip)");
            Assert.AreEqual(true, RecenterTracker.IsUnguided(Guida(lost: null, state: "STAR_LOST")), "Agente prima del 3.1");
            Assert.AreEqual(true, RecenterTracker.IsUnguided(Guida(reachable: false)), "nessuna notizia: prudente");
            Assert.IsNull(RecenterTracker.IsUnguided(Guida(valid: false)), "payload incompleto: non si conta");
        }

        [TestMethod]
        public void SogliaSottoUnMinuto_TrattataComeUnMinuto()
        {
            var t = Connesso();
            var now = Run(t, 0, 1, internalSafe: false, unguided: true);
            Run(t, now, 1, internalSafe: true, unguided: false);
            Assert.IsTrue(t.Decide(0).Check);
        }
    }
}
