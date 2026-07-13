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
}
