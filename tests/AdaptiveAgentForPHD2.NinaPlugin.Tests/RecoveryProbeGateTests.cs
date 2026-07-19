#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Reflection;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §57 — casi 6-9 del prompt sul gate posa-sonda (logica pura, come per
    /// SafetyDecisionEngine). L'istruzione WaitForRecoveryHint e' un guscio sottile
    /// attorno a RecoveryProbeGate.Evaluate.
    /// </summary>
    [TestClass]
    public sealed class RecoveryProbeGateTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(12);
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Long = TimeSpan.FromHours(1);

        // ---- Caso 6: S1 standalone — con hint spento/assente apre SOLO a timeout ----
        [TestMethod]
        public void S1_Standalone_OpensOnlyAtTimeout()
        {
            // Prima del timeout: chiuso.
            var (open, _) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(11), Long, hintActive: false, Timeout, MinInterval);
            Assert.IsFalse(open);
            // Al timeout: aperto, attribuzione S1.
            (open, var reason) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(12), Long, hintActive: false, Timeout, MinInterval);
            Assert.IsTrue(open);
            Assert.AreEqual(RecoveryProbeGate.ReasonTimeout, reason);
        }

        // ---- Caso 7 (paletto 3): l'hint NON apre prima del min-interval ----
        [TestMethod]
        public void MinInterval_Floors_EvenWithActiveHint()
        {
            var (open, reason) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3),   // ultima sonda 3 min fa
                hintActive: true, Timeout, MinInterval);
            Assert.IsFalse(open, "hint ballerino: mai sotto il floor");
            StringAssert.Contains(reason, "min-interval");
            // Superato il floor: l'hint apre subito (anticipo S2).
            (open, reason) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
                hintActive: true, Timeout, MinInterval);
            Assert.IsTrue(open);
            Assert.AreEqual(RecoveryProbeGate.ReasonHint, reason);
        }

        // ---- Paletto 3 vale anche per S1 (timeout scaduto ma sonda troppo recente) ----
        [TestMethod]
        public void MinInterval_Floors_TimeoutToo()
        {
            var (open, _) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1),
                hintActive: false, Timeout, MinInterval);
            Assert.IsFalse(open);
        }

        // ---- Paletto 1: l'hint puo' solo ANTICIPARE, mai sopprimere il timeout ----
        [TestMethod]
        public void Hint_NeverSuppresses_Timeout()
        {
            // hint inattivo + timeout scaduto => apre comunque (S1 e' l'autorita').
            var (open, reason) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(13), Long, hintActive: false, Timeout, MinInterval);
            Assert.IsTrue(open);
            Assert.AreEqual(RecoveryProbeGate.ReasonTimeout, reason);
        }

        // ---- Caso 9: attribuzione telemetria (S2 quando anticipa, S1 a timeout) ----
        [TestMethod]
        public void Reasons_Distinguish_S1_From_S2()
        {
            var (_, s2) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(6), Long, hintActive: true, Timeout, MinInterval);
            var (_, s1) = RecoveryProbeGate.Evaluate(
                TimeSpan.FromMinutes(12), Long, hintActive: false, Timeout, MinInterval);
            StringAssert.Contains(s2, "S2");
            StringAssert.Contains(s1, "S1");
            Assert.AreNotEqual(s1, s2);
        }

        // ---- Caso 8 (paletto §57-bis): niente cattura AUTONOMA ----
        // Dopo il Gate §57-bis la RecoveryProbe cattura internamente (vincolo GUI: i
        // container del Trigger On Unsafe rifiutano le istruzioni Camera) — ma SOLO
        // dentro Execute() eseguito dal sequencer: nessun timer/trigger autonomo.
        [TestMethod]
        public void Instruction_HasNoAutonomousCaptureMachinery()
        {
            var t = typeof(RecoveryProbe);
            var fieldTypes = t.GetFields(BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.NonPublic | BindingFlags.Public)
                              .Select(f => f.FieldType.Name).ToList();
            foreach (var name in fieldTypes)
            {
                StringAssert.DoesNotMatch(name,
                    new System.Text.RegularExpressions.Regex("^Timer$|DispatcherTimer|Thread$"),
                    $"meccanismo autonomo trovato: {name}");
            }
            // La memoria del light (LastLightTracker) NON deve poter catturare:
            // nessun riferimento a IImagingMediator (e' sola memoria da ImageSaved).
            var trackerFields = typeof(LastLightTracker)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType.Name).ToList();
            foreach (var name in trackerFields)
            {
                StringAssert.DoesNotMatch(name,
                    new System.Text.RegularExpressions.Regex("ImagingMediator"),
                    $"il tracker non deve catturare: {name}");
            }
        }

        // ---- §57-bis: la memoria del light interrotto (fonte parametri della sonda) ----
        [TestMethod]
        public void LastLightMemory_StoresAndClearsProfile()
        {
            LastLightMemory.Clear();
            Assert.IsNull(LastLightMemory.Current);
            var p = new LastLightProfile(300.0, 100, 50, 1, 1, "L", DateTimeOffset.UtcNow);
            LastLightMemory.Update(p);
            Assert.AreEqual(300.0, LastLightMemory.Current!.ExposureSeconds);
            Assert.AreEqual(100, LastLightMemory.Current!.Gain);
            Assert.AreEqual("L", LastLightMemory.Current!.Filter);
            LastLightMemory.Clear();
            Assert.IsNull(LastLightMemory.Current);
        }
    }

    /// <summary>
    /// §64 — cadenza AUTOMATICA della sonda: timeout = finestra §43 − posa, clampata.
    /// L'invariante che conta: (timeout + posa) non deve MAI superare la finestra di
    /// freschezza, altrimenti tra due sonde la telemetria di N1 diventa stantia.
    /// </summary>
    [TestClass]
    public sealed class AdaptiveTimeoutTests
    {
        private static double Auto(double? window, double exposure) =>
            RecoveryProbeGate.AdaptiveTimeout(window, exposure).TotalSeconds;

        [TestMethod]
        public void UsesAgentWindow_MinusExposure()
        {
            // finestra reale 180 s (posa 60 s, floor §43) => 120 s di attesa
            Assert.AreEqual(120, Auto(180, 60), 0.01);
            // sub lungo: finestra 1.5x450 => 450 s, attesa 150 s
            Assert.AreEqual(150, Auto(450, 300), 0.01);
        }

        [TestMethod]
        public void LongSubs_CappedAtFieldValidatedTarget()
        {
            // §64-v2 — la finestra e' un TETTO, non un'uguaglianza: quando consente di
            // piu', l'attesa si ferma al target validato sul cielo (3 min), non al tetto.
            Assert.AreEqual(RecoveryProbeGate.TargetSeconds, Auto(900, 600), 0.01);   // v1 dava 300 s
            Assert.AreEqual(RecoveryProbeGate.TargetSeconds, Auto(1800, 1200), 0.01); // sub estremi: idem
            // Sub corti: vincola la finestra (sotto il target), come in v1.
            Assert.IsTrue(Auto(180, 60) < RecoveryProbeGate.TargetSeconds);
        }

        [TestMethod]
        public void CycleNeverExceedsFreshnessWindow()
        {
            // L'invariante di progetto, su tutta la gamma di pose realistiche.
            foreach (var exposure in new double[] { 10, 30, 60, 120, 180, 300, 600 })
            {
                var window = Math.Max(180.0, 1.5 * exposure);   // §43
                var cycle = Auto(window, exposure) + exposure;
                Assert.IsTrue(cycle <= window + 0.001,
                    $"posa {exposure}s: ciclo {cycle}s supera la finestra {window}s");
            }
        }

        [TestMethod]
        public void FallsBackToLocalFormula_WhenAgentWindowUnavailable()
        {
            // agente offline / N1 spento: stessa formula §43 con i default noti.
            Assert.AreEqual(120, Auto(null, 60), 0.01);      // max(180, 90) - 60
            Assert.AreEqual(150, Auto(null, 300), 0.01);     // max(180, 450) - 300
            Assert.AreEqual(Auto(null, 60), Auto(0, 60), 0.01);        // 0 => non valido
            Assert.AreEqual(Auto(null, 60), Auto(double.NaN, 60), 0.01);
        }

        [TestMethod]
        public void ClampsToFloor_AndTargetReplacesCeiling()
        {
            Assert.AreEqual(RecoveryProbeGate.AutoFloorSeconds, Auto(100, 90), 0.01);   // 10 s => floor
            // §64-v2: una finestra enorme non allunga piu' l'attesa — vince il target.
            Assert.AreEqual(RecoveryProbeGate.TargetSeconds, Auto(5000, 600), 0.01);    // v1 dava il ceiling 900
            Assert.IsTrue(RecoveryProbeGate.TargetSeconds < RecoveryProbeGate.AutoCeilingSeconds,
                "il ceiling resta come rete di sicurezza sopra il target");
        }

        [TestMethod]
        public void NeverReturnsNonPositive_EvenOnAbsurdInput()
        {
            foreach (var (w, e) in new (double?, double)[] { (1, 10000), (null, 0), (-5, 60), (10, 10) })
            {
                Assert.IsTrue(RecoveryProbeGate.AdaptiveTimeout(w, e).TotalSeconds
                              >= RecoveryProbeGate.AutoFloorSeconds);
            }
        }
    }
}
