#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// Fix N6 (validazione 2026-07-10) e regressioni dei latch che non dipendono da chi
    /// giudica il cielo: AGENT_LOST, STAR_LOST dallo stato del controller (Agente prima del
    /// 3.1, §65), GUIDE_UNOBSERVABLE (§68), payload malformato.
    /// §128 (1.15) — tolti i test del giudizio della camera di ripresa (indice, telemetria
    /// stantia, kill-switch della logica a indice, sensore rapido accanto a quello lento):
    /// quel percorso non esiste piu'. Dove serviva un latch del cielo, ora e' quello della
    /// guida. Il giudice guida ha i suoi test: GuideSkyJudgeTests.
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
            public int ClearSafePolls { get; set; } = 4;
            public int SkyDegradingUnsafePolls { get; set; } = 8;
            public bool AgentLostUnsafeEnabled { get; set; } = true;
            public int AgentLostUnsafePolls { get; set; } = 4;
            public bool GuideUnobservableEnabled { get; set; } = true;
            public int GuideSilenceSeconds { get; set; } = 90;
            public int GuideUnobservablePolls { get; set; } = 3;
        }

        /// <summary>Snapshot di un Agente SENZA giudice del cielo e senza la stella persa
        /// dagli eventi (prima del 3.1): restano i latch indipendenti dal cielo.</summary>
        private static AgentStatusSnapshot Snap(
            string? guiding = "NORMAL", bool valid = true, bool reachable = true,
            double? guideFrameAge = null, bool guidingExpected = false,
            int starErrors = 0, bool alertSevere = false)
            => new(guiding, valid, null, false, null, null, reachable,
                   GuideFrameAgeS: guideFrameAge, GuidingExpected: guidingExpected,
                   GuideStarErrorsRecent: starErrors, GuideAlertSevere: alertSevere);

        /// <summary>Snapshot con il giudice del cielo (Agente 3.1+): degrado o sereno dalla guida.</summary>
        private static AgentStatusSnapshot Cielo(bool degrading, bool skyOk, string guiding = "NORMAL",
            double? guideFrameAge = 2, bool guidingExpected = true)
            => new(guiding, true, null, false, null, null, true,
                   GuideFrameAgeS: guideFrameAge, GuidingExpected: guidingExpected,
                   SkyDegrading: degrading, StarLost: false, StarTracked: true, SkyOk: skyOk,
                   ChannelReady: true, GuideJudgeReady: true);

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

        // ---- agente irraggiungibile a sessione attiva => UNSAFE, mai SAFE ----
        [TestMethod]
        public void AgentLost_ActiveSession_TriggersUnsafe_NeverSafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "NORMAL"), s);
            for (int i = 0; i < s.AgentLostUnsafePolls - 1; i++)
            {
                Assert.AreEqual(SafetyDecision.NoChange, e.Evaluate(Snap(reachable: false, valid: false), s));
            }
            Assert.AreEqual(SafetyDecision.BecameUnsafe, e.Evaluate(Snap(reachable: false, valid: false), s));
            Assert.AreEqual(SafetyCause.AgentLost, e.LastCause);
            // Da UNSAFE, l'irraggiungibilita' prolungata non deve MAI produrre SAFE.
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Snap(reachable: false, valid: false), 30));
        }

        // ---- latch esistente preservato durante l'irraggiungibilita' ----
        [TestMethod]
        public void AgentLost_PreservesExistingUnsafeLatch()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Cielo(degrading: true, skyOk: false), s.SkyDegradingUnsafePolls));  // nubi
            var d = Run(e, s, Snap(reachable: false, valid: false), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "perdere l'agente non deve mai rilasciare un UNSAFE");
        }

        // ---- agente offline a guida ferma (fine sessione) => nessun falso allarme ----
        [TestMethod]
        public void AgentLost_InactiveSession_NoFalseAlarm()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "INACTIVE"), s);   // guida ferma volontariamente
            var d = Run(e, s, Snap(reachable: false, valid: false), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "fine sessione: agente offline e' normale");
        }

        // ---- rientro dopo agent-lost: lo concede il sereno della guida, non il ritorno dei dati ----
        [TestMethod]
        public void AgentLost_Recovery_RequiresClearEvidence()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Cielo(degrading: false, skyOk: true), s);
            Run(e, s, Snap(reachable: false, valid: false), s.AgentLostUnsafePolls);   // UNSAFE (AgentLost)
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Cielo(degrading: false, skyOk: false), 10), "il rientro non e' gratis");
            var d = Run(e, s, Cielo(degrading: false, skyOk: true), s.ClearSafePolls);
            Assert.AreEqual(SafetyDecision.BecameSafe, d);
        }

        // ---- agent-lost senza giudice del cielo: rientra con la stella tracciata ----
        [TestMethod]
        public void AgentLost_WithoutSkyJudge_ReleasesOnTrackedStar()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            e.Evaluate(Snap(guiding: "NORMAL"), s);
            Run(e, s, Snap(reachable: false, valid: false), s.AgentLostUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameSafe,
                Run(e, s, Snap(guiding: "NORMAL"), SafetyDecisionEngine.ResumeTicks));
        }

        // ---- STAR_LOST dallo stato del controller (Agente prima del 3.1) ----
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

        // ---- §65: rientro su guida OPERATIVA (non su guida "eccellente") ----------------

        /// <summary>
        /// Il caso REALE della notte 2026-07-19: cielo limpido, stella ri-tracciata, ma
        /// RMS nella banda neutra => l'agente non riporta mai NORMAL e prima del §65 il
        /// latch restava appeso (18 minuti misurati). Ora DEGRADED sblocca il rientro.
        /// </summary>
        [TestMethod]
        public void StarLost_ResumesOn_Degraded_TheFieldCase()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { StarLostConsolidationSeconds = 60, HealthCheckIntervalSeconds = 15 };
            Assert.AreEqual(SafetyDecision.BecameUnsafe, Run(e, s, Snap(guiding: "STAR_LOST"), 4));
            var d = Run(e, s, Snap(guiding: "DEGRADED"), SafetyDecisionEngine.ResumeTicks);
            Assert.AreEqual(SafetyDecision.BecameSafe, d,
                "stella ri-tracciata (DEGRADED) = guida operativa: il latch deve sbloccarsi");
        }

        [TestMethod]
        public void StarLost_ResumesOn_EveryTrackedState()
        {
            // Coerenza col criterio: se uno stato non basta a FAR SCATTARE l'unsafe,
            // non deve poterlo MANTENERE. Nessuno di questi genera UNSAFE da solo.
            foreach (var guiding in new[] { "NORMAL", "RECOVERING", "DEGRADED", "CRITICAL" })
            {
                var e = new SafetyDecisionEngine();
                var s = new FakeSettings { StarLostConsolidationSeconds = 60, HealthCheckIntervalSeconds = 15 };
                Run(e, s, Snap(guiding: "STAR_LOST"), 4);
                Assert.AreEqual(SafetyDecision.BecameSafe,
                    Run(e, s, Snap(guiding: guiding), SafetyDecisionEngine.ResumeTicks),
                    $"stato '{guiding}': stella tracciata => guida operativa");
            }
        }

        [TestMethod]
        public void StarLost_DoesNotResumeOn_InactiveOrNullOrStarLost()
        {
            // Fail-safe: guida ferma (INACTIVE) o payload senza stato NON sono evidenza
            // che la stella sia tracciata => il latch RESTA.
            foreach (var guiding in new string?[] { "INACTIVE", null })
            {
                var e = new SafetyDecisionEngine();
                var s = new FakeSettings { StarLostConsolidationSeconds = 60, HealthCheckIntervalSeconds = 15 };
                Run(e, s, Snap(guiding: "STAR_LOST"), 4);
                Assert.AreEqual(SafetyDecision.NoChange,
                    Run(e, s, Snap(guiding: guiding), SafetyDecisionEngine.ResumeTicks * 4),
                    $"stato '{guiding ?? "null"}': nessuna evidenza di stella tracciata => resta UNSAFE");
            }
        }

        [TestMethod]
        public void StarLost_StillRequiresHysteresis()
        {
            // Il §65 cambia QUALI stati contano, non la durata: l'isteresi resta.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { StarLostConsolidationSeconds = 60, HealthCheckIntervalSeconds = 15 };
            Run(e, s, Snap(guiding: "STAR_LOST"), 4);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Snap(guiding: "DEGRADED"), SafetyDecisionEngine.ResumeTicks - 1),
                "sotto ResumeTicks non si rientra");
            // e una ricaduta in STAR_LOST azzera lo streak
            Run(e, s, Snap(guiding: "STAR_LOST"), 1);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Snap(guiding: "DEGRADED"), SafetyDecisionEngine.ResumeTicks - 1));
        }

        // ---- §68: osservabilita' del canale di guida -------------------------------

        /// <summary>
        /// IL CASO REALE del 2026-07-26: la camera di guida entra in stato patologico,
        /// PHD2 smette di consegnare frame e `guiding_state` resta CONGELATO su un valore
        /// operativo (CRITICAL). Prima del §68 nessun latch poteva scattare e il monitor
        /// sarebbe rimasto SAFE tutta la notte.
        /// </summary>
        [TestMethod]
        public void GuideChannelSilent_WhileGuidingExpected_BecomesUnsafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };
            var frozen = Snap(guiding: "CRITICAL", guideFrameAge: 300, guidingExpected: true);
            var d = Run(e, s, frozen, s.GuideUnobservablePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.GuideUnobservable, e.LastCause);
        }

        [TestMethod]
        public void AnnouncedPause_NeverRaisesGuideAlarm()
        {
            // Flip/autofocus/stop manuale: PHD2 ANNUNCIA la pausa => guidingExpected=false.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 3600, guidingExpected: false), 40);
            Assert.AreEqual(SafetyDecision.NoChange, d);
        }

        [TestMethod]
        public void FreshFrames_KeepChannelSafe_AndDrainTheAccumulator()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };
            Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 200, guidingExpected: true), 2);
            var d = Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 2, guidingExpected: true), 5);
            Assert.AreEqual(SafetyDecision.NoChange, d);
        }

        [TestMethod]
        public void ChannelRecovery_ReleasesTheLatch()
        {
            // Senza giudice del cielo i frame che tornano bastano (con il giudice, il SAFE
            // aspetta anche il sereno: GuideSkyJudgeTests.CanaleMuto_RientraSoloConIlSereno).
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 300, guidingExpected: true), 3));
            Assert.AreEqual(SafetyDecision.BecameSafe,
                Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 1, guidingExpected: true), 3));
        }

        [TestMethod]
        public void Corroboration_HalvesTheThreshold_ButNeverTriggersAlone()
        {
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };

            // 50 s di silenzio: SOTTO la soglia piena (90) => nessun allarme...
            var quiet = new SafetyDecisionEngine();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(quiet, s, Snap(guiding: "NORMAL", guideFrameAge: 50, guidingExpected: true), 10));

            // ...ma con Alert PHD2 severo la soglia si dimezza (45) e scatta.
            var corroborated = new SafetyDecisionEngine();
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(corroborated, s, Snap(guiding: "NORMAL", guideFrameAge: 50,
                                          guidingExpected: true, alertSevere: true), 3));

            // La sola corroborazione, SENZA silenzio, non decide nulla (paletto §57).
            var alertOnly = new SafetyDecisionEngine();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(alertOnly, s, Snap(guiding: "NORMAL", guideFrameAge: 2, guidingExpected: true,
                                       alertSevere: true, starErrors: 20), 20));
        }

        [TestMethod]
        public void KillSwitch_And_OldAgent_LeaveTheLatchInert()
        {
            var e1 = new SafetyDecisionEngine();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e1, new FakeSettings { GuideUnobservableEnabled = false },
                    Snap(guiding: "NORMAL", guideFrameAge: 9999, guidingExpected: true), 20));

            // Agente <v2.9: non espone il blocco => GuideFrameAgeS null => inerte
            var e2 = new SafetyDecisionEngine();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e2, new FakeSettings(), Snap(guiding: "NORMAL", guideFrameAge: null,
                                                 guidingExpected: true), 20));
        }

        [TestMethod]
        public void GuideLatch_DoesNotDisturbTheOtherLatches()
        {
            // Il §68 non deve alterare il percorso delle nubi: frame freschi, nubi vere.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Cielo(degrading: true, skyOk: false), s.SkyDegradingUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
        }

        // ---- payload malformato con agente raggiungibile => no-op ----
        [TestMethod]
        public void InvalidPayload_Reachable_IsNoOp()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Run(e, s, Cielo(degrading: true, skyOk: false), s.SkyDegradingUnsafePolls); // UNSAFE (nubi)
            var d = Run(e, s, Snap(guiding: null, valid: false, reachable: true), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "payload transitoriamente malformato: mantieni lo stato");
        }
    }
}
