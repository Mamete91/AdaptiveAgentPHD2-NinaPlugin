#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// Fix N6 (validazione 2026-07-10): gli 8 casi del prompt + regressioni.
    /// L'engine e' puro (snapshot + ISafetySettings => decisione): niente NINA statics.
    /// </summary>
    [TestClass]
    public sealed class SafetyDecisionEngineTests
    {
        /// <summary>Stub con i default di produzione (PluginSettings.Default*).</summary>
        private sealed class FakeSettings : ISafetySettings
        {
            public int HealthCheckIntervalSeconds { get; set; } = 15;
            public int StarLostConsolidationSeconds { get; set; } = 300;
            public bool CloudSafetyEnabled { get; set; } = true;
            public int CloudUnsafePolls { get; set; } = 8;
            public int ClearSafePolls { get; set; } = 4;
            public bool UseIndexCloudLogic { get; set; } = true;
            public double CloudIndexAccumulateBelow { get; set; } = 0.5;
            public double CloudIndexDrainAbove { get; set; } = 0.8;
            public bool StaleUnsafeEnabled { get; set; } = true;
            public int StaleUnsafePolls { get; set; } = 8;
            public bool AgentLostUnsafeEnabled { get; set; } = true;
            public int AgentLostUnsafePolls { get; set; } = 4;
        }

        private static AgentStatusSnapshot Snap(
            string? guiding = "NORMAL", bool valid = true, string? state = null,
            bool fresh = false, double? index = null, double? age = null, bool reachable = true)
            => new(guiding, valid, state, fresh, index, age, reachable);

        /// <summary>Esegue n tick identici e ritorna l'ultima decisione non-NoChange (o NoChange).</summary>
        private static SafetyDecision Run(SafetyDecisionEngine e, ISafetySettings s,
                                          AgentStatusSnapshot snap, int n)
        {
            var last = SafetyDecision.NoChange;
            for (int i = 0; i < n; i++)
            {
                var d = e.Evaluate(snap, s);
                if (d != SafetyDecision.NoChange) { last = d; }
            }
            return last;
        }

        // ---- Caso 1: flicker CLOUD/HAZE (come la notte del 2026-07-10) => UNSAFE ----
        [TestMethod]
        public void Flicker_CloudHaze_TriggersUnsafe_WithIndexLogic()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var decision = SafetyDecision.NoChange;
            // Pattern della notte: ~2 poll CLOUD, 1 rimbalzo HAZE, ripetuto. Con la logica
            // legacy il rimbalzo azzerava lo streak (mai UNSAFE); col leaky accumula.
            for (int i = 0; i < 15 && decision != SafetyDecision.BecameUnsafe; i++)
            {
                double idx = (i % 3 == 2) ? 0.6 : 0.3;   // 0.6 = zona HAZE (neutra), 0.3 = accumula
                string st = (i % 3 == 2) ? "HAZE" : "CLOUD";
                var d = e.Evaluate(Snap(state: st, fresh: true, index: idx), s);
                if (d != SafetyDecision.NoChange) { decision = d; }
            }
            Assert.AreEqual(SafetyDecision.BecameUnsafe, decision, "il flicker deve accumulare, non azzerare");
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
        }

        // ---- Caso 2: stantio oltre finestra + contesto degradato + sessione attiva => UNSAFE ----
        [TestMethod]
        public void StaleTelemetry_DegradedContext_ActiveSession_TriggersUnsafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            // Ultimo contesto affidabile: CLOUD profondo (come indice congelato a 0.08).
            e.Evaluate(Snap(state: "CLOUD", fresh: true, index: 0.08), s);
            // Telemetria si ferma (fresh=false = oltre la finestra adattiva §43).
            var d = Run(e, s, Snap(state: "CLOUD", fresh: false, index: null), s.StaleUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d, "stantio a contesto degradato deve escalare");
            Assert.AreEqual(SafetyCause.StaleTelemetry, e.LastCause);
        }

        // ---- Caso 3: nessun falso allarme se il contesto NON era degradato ----
        [TestMethod]
        public void StaleTelemetry_ClearContext_NoFalseAlarm()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(state: "CLEAR", fresh: true, index: 0.95), s);
            var d = Run(e, s, Snap(state: null, fresh: false), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "cielo sereno + telemetria persa: nessun allarme");
        }

        // ---- Caso 4: indice 0.08 sostenuto => UNSAFE in CloudUnsafePolls (2 min) ----
        [TestMethod]
        public void SustainedLowIndex_TriggersUnsafe_InExpectedTime()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            for (int i = 0; i < s.CloudUnsafePolls - 1; i++)
            {
                Assert.AreEqual(SafetyDecision.NoChange,
                    e.Evaluate(Snap(state: "CLOUD", fresh: true, index: 0.08), s));
            }
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                e.Evaluate(Snap(state: "CLOUD", fresh: true, index: 0.08), s));
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
        }

        // ---- Caso 5: recupero (indice alto stabile) => SAFE con isteresi ClearSafePolls ----
        [TestMethod]
        public void Recovery_HighIndex_BecomesSafe_WithHysteresis()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08), s.CloudUnsafePolls);
            // Drena a rate CloudUnsafePolls/ClearSafePolls = 2/poll: SAFE al 4° poll CLEAR.
            for (int i = 0; i < s.ClearSafePolls - 1; i++)
            {
                Assert.AreEqual(SafetyDecision.NoChange,
                    e.Evaluate(Snap(state: "CLEAR", fresh: true, index: 0.9), s));
            }
            Assert.AreEqual(SafetyDecision.BecameSafe,
                e.Evaluate(Snap(state: "CLEAR", fresh: true, index: 0.9), s));
        }

        // ---- Caso 6: kill-switch => comportamento IDENTICO alla logica attuale ----
        [TestMethod]
        public void KillSwitchesOff_LegacyBehavior_HazeResetsStreak()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings
            {
                UseIndexCloudLogic = false,
                StaleUnsafeEnabled = false,
                AgentLostUnsafeEnabled = false,
            };
            // Legacy Bug A riprodotto: 7 CLOUD, 1 HAZE (reset), 7 CLOUD => mai UNSAFE.
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Snap(state: "CLOUD", fresh: true), 7));
            Assert.AreEqual(SafetyDecision.NoChange, e.Evaluate(Snap(state: "HAZE", fresh: true), s));
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Snap(state: "CLOUD", fresh: true), 7));
            // Legacy: stantio azzera e non escala.
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Snap(state: "CLOUD", fresh: false), 30));
            // Legacy: 8 CLOUD consecutivi freschi => UNSAFE (il percorso storico funziona ancora).
            Assert.AreEqual(SafetyDecision.BecameUnsafe, Run(e, s, Snap(state: "CLOUD", fresh: true), 8));
        }

        // ---- Caso 7: agente irraggiungibile a sessione attiva => UNSAFE, mai SAFE ----
        [TestMethod]
        public void AgentLost_ActiveSession_TriggersUnsafe_NeverSafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "NORMAL", state: "CLEAR", fresh: true, index: 0.9), s);
            for (int i = 0; i < s.AgentLostUnsafePolls - 1; i++)
            {
                Assert.AreEqual(SafetyDecision.NoChange, e.Evaluate(Snap(reachable: false, valid: false), s));
            }
            Assert.AreEqual(SafetyDecision.BecameUnsafe, e.Evaluate(Snap(reachable: false, valid: false), s));
            Assert.AreEqual(SafetyCause.AgentLost, e.LastCause);
            // Da UNSAFE, l'irraggiungibilita' prolungata non deve MAI produrre SAFE.
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Snap(reachable: false, valid: false), 30));
        }

        // ---- Caso 7-bis: latch esistente preservato durante l'irraggiungibilita' ----
        [TestMethod]
        public void AgentLost_PreservesExistingUnsafeLatch()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08), s.CloudUnsafePolls); // UNSAFE (Cloud)
            var d = Run(e, s, Snap(reachable: false, valid: false), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "perdere l'agente non deve mai rilasciare un UNSAFE");
        }

        // ---- Caso 8: agente offline a guida ferma (fine sessione) => nessun falso allarme ----
        [TestMethod]
        public void AgentLost_InactiveSession_NoFalseAlarm()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "INACTIVE"), s);   // guida ferma volontariamente
            var d = Run(e, s, Snap(reachable: false, valid: false), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "fine sessione: agente offline e' normale");
        }

        // ---- Recupero dopo agent-lost: il rientro passa dall'evidenza CLEAR (isteresi) ----
        [TestMethod]
        public void AgentLost_Recovery_RequiresClearEvidence()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "NORMAL", state: "CLEAR", fresh: true, index: 0.9), s);
            Run(e, s, Snap(reachable: false, valid: false), s.AgentLostUnsafePolls);   // UNSAFE (AgentLost)
            // L'agente torna con dati freschi CLEAR: il latch si trasferisce sul percorso CLOUD
            // (degrado saturato) => servono ClearSafePolls di evidenza per SAFE. Non istantaneo.
            Assert.AreEqual(SafetyDecision.NoChange,
                e.Evaluate(Snap(state: "CLEAR", fresh: true, index: 0.9), s), "il rientro non e' gratis");
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.9), s.ClearSafePolls);
            Assert.AreEqual(SafetyDecision.BecameSafe, d);
        }

        // ---- Regressione: STAR_LOST invariato (consolidamento => UNSAFE; NORMAL => SAFE) ----
        [TestMethod]
        public void StarLost_Consolidation_Unchanged()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { StarLostConsolidationSeconds = 60, HealthCheckIntervalSeconds = 15 };
            var d = Run(e, s, Snap(guiding: "STAR_LOST"), 4);   // 60/15 = 4 tick
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.StarLost, e.LastCause);
            d = Run(e, s, Snap(guiding: "NORMAL"), SafetyDecisionEngine.ResumeTicks);
            Assert.AreEqual(SafetyDecision.BecameSafe, d);
        }

        // ---- Regressione: payload malformato con agente raggiungibile => no-op ----
        [TestMethod]
        public void InvalidPayload_Reachable_IsNoOp()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08), s.CloudUnsafePolls); // UNSAFE
            var d = Run(e, s, Snap(guiding: null, valid: false, reachable: true), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "payload transitoriamente malformato: mantieni lo stato");
        }
    }
}
