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
            public bool GuideUnobservableEnabled { get; set; } = true;
            public int GuideSilenceSeconds { get; set; } = 90;
            public int GuideUnobservablePolls { get; set; } = 3;
            public bool SkyDegradingAccumulateEnabled { get; set; } = true;
            public int SkyDegradingUnsafePolls { get; set; } = 8;
            // §126 — questi test fissano il comportamento con la CAMERA giudice (fino al
            // 1.13, oggi opzione). Il giudice guida ha i suoi test: GuideSkyJudgeTests.
            public bool ImagingCameraUnsafeEnabled { get; set; } = true;
        }

        private static AgentStatusSnapshot Snap(
            string? guiding = "NORMAL", bool valid = true, string? state = null,
            bool fresh = false, double? index = null, double? age = null, bool reachable = true,
            double? guideFrameAge = null, bool guidingExpected = false,
            int starErrors = 0, bool alertSevere = false, bool skyDegrading = false)
            => new(guiding, valid, state, fresh, index, age, reachable,
                   GuideFrameAgeS: guideFrameAge, GuidingExpected: guidingExpected,
                   GuideStarErrorsRecent: starErrors, GuideAlertSevere: alertSevere,
                   SkyDegrading: skyDegrading);

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
        /// operativo (CRITICAL). Prima del §68 nessuno dei quattro latch poteva scattare
        /// e il monitor sarebbe rimasto SAFE tutta la notte.
        /// </summary>
        [TestMethod]
        public void GuideChannelSilent_WhileGuidingExpected_BecomesUnsafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };
            // stato congelato su CRITICAL, cielo limpido, agente raggiungibile: tutto
            // "normale" per gli altri latch — l'unico segnale e' il silenzio del canale.
            var frozen = Snap(guiding: "CRITICAL", state: "CLEAR", fresh: true, index: 0.92,
                              guideFrameAge: 300, guidingExpected: true);
            var d = Run(e, s, frozen, s.GuideUnobservablePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.GuideUnobservable, e.LastCause);
        }

        [TestMethod]
        public void AnnouncedPause_NeverRaisesGuideAlarm()
        {
            // Flip/autofocus/stop manuale: PHD2 ANNUNCIA la pausa => guidingExpected=false.
            // Nessun allarme per quanto a lungo duri il silenzio.
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
            // due tick di silenzio (sotto il cap), poi i frame tornano: niente UNSAFE
            Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 200, guidingExpected: true), 2);
            var d = Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 2, guidingExpected: true), 5);
            Assert.AreEqual(SafetyDecision.NoChange, d);
        }

        [TestMethod]
        public void ChannelRecovery_ReleasesTheLatch()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { GuideSilenceSeconds = 90, GuideUnobservablePolls = 3 };
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Snap(guiding: "NORMAL", guideFrameAge: 300, guidingExpected: true), 3));
            // i frame tornano a fluire = evidenza POSITIVA di osservabilita'
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
            // kill-switch esplicito
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
            // Il §68 non deve alterare il percorso nubi gia' validato.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08,
                                   guideFrameAge: 2, guidingExpected: true), s.CloudUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
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
    
        // ---- §76: il sensore VELOCE accanto a quello lento ----

        [TestMethod]
        public void SkyDegrading_AccumulatesTowardUnsafe_WhileIndexStillLooksClear()
        {
            // Il caso della notte 4/8: N1 e' ancora fermo sull'ultima posa BUONA
            // (indice 0.95) mentre il canale guida vede gia' il crollo.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95,
                                   skyDegrading: true), s.CloudUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d,
                "il sensore veloce deve poter anticipare l'unsafe");
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
        }

        [TestMethod]
        public void SkyDegrading_NeverDrainsTowardSafe()
        {
            // IL PALETTO CENTRALE: una stella sola non riporta al sicuro.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08), s.CloudUnsafePolls);

            // Cielo tornato limpido per N1 MA il canale guida dice ancora degrado:
            // il drain e' bloccato, si resta unsafe.
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95,
                                   skyDegrading: true), 20);
            Assert.AreEqual(SafetyDecision.NoChange, d,
                "con degrado in corso il drain e' sospeso: nessun rientro");

            // Quando anche il canale guida si tranquillizza, il rientro riprende
            // dal percorso NORMALE (la posa-sonda resta il giudice).
            var d2 = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95), s.ClearSafePolls);
            Assert.AreEqual(SafetyDecision.BecameSafe, d2);
        }

        [TestMethod]
        public void SkyDegrading_KillSwitch_RestoresPre76Behaviour()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { SkyDegradingAccumulateEnabled = false };
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95,
                                   skyDegrading: true), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d);
        }

        [TestMethod]
        public void SkyDegrading_WorksEvenWithoutTransparencyData()
        {
            // Nessun indice, nessuno stato: il veloce e' l'unico che parla.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Snap(state: null, fresh: false, index: null,
                                   skyDegrading: true), s.CloudUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
        }

        [TestMethod]
        public void OldAgent_WithoutTheSignal_BehavesExactlyAsBefore()
        {
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95), 30);
            Assert.AreEqual(SafetyDecision.NoChange, d, "fail-inert su Agenti <v2.14");
        }

        // ---- §79: RAPIDITA' e PERSISTENZA sono dimensioni diverse ----
        // Prima i due percorsi condividevano accumulatore e tetto: tarare l'uno
        // muoveva l'altro. Questi test blindano l'indipendenza appena conquistata.

        [TestMethod]
        public void RaisingPersistenceThreshold_DoesNotSlowTheFastPath()
        {
            // LA GARANZIA per la taratura in programma: portare la persistenza del
            // cielo da 2 a 5 minuti (8 -> 20 poll) non deve rallentare il canale
            // guida, che esiste PROPRIO per anticipare la camera. Con l'accumulatore
            // condiviso qui sarebbero serviti 20 poll invece di 8.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { CloudUnsafePolls = 20, SkyDegradingUnsafePolls = 8 };
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95,
                                   skyDegrading: true), 8);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d,
                "il percorso rapido deve conservare la propria soglia");
        }

        [TestMethod]
        public void RaisingFastThreshold_DoesNotSlowThePersistentPath()
        {
            // E la simmetrica: il tetto del canale guida non tocca la camera.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { CloudUnsafePolls = 8, SkyDegradingUnsafePolls = 40 };
            var d = Run(e, s, Snap(state: "CLOUD", fresh: true, index: 0.08), 8);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d,
                "la camera di ripresa ha la sua soglia di persistenza");
        }

        [TestMethod]
        public void FastPathAlone_NeverGrantsSafe_WithoutCameraEvidence()
        {
            // L'asimmetria FISICA sopravvive alla separazione: la stella di guida
            // arma l'unsafe ma non lo disarma. A disarmarlo e' solo la camera di
            // ripresa — cioe' la posa-sonda del recupero.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { CloudUnsafePolls = 20, SkyDegradingUnsafePolls = 8 };
            Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95, skyDegrading: true), 8);

            // Il canale guida si tranquillizza, ma NON arriva nessuna posa.
            var d = Run(e, s, Snap(fresh: true), 60);
            Assert.AreEqual(SafetyDecision.NoChange, d,
                "senza evidenza della camera non si rientra");
        }

        [TestMethod]
        public void CameraEvidence_ReleasesBothAccumulators()
        {
            // Il drenaggio si scala sul proprio tetto: ClearSafePolls conserva il suo
            // significato ("N poll di sereno per rientrare") su ENTRAMBI i percorsi,
            // quale che sia il rapporto fra le due soglie.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings { CloudUnsafePolls = 20, SkyDegradingUnsafePolls = 8 };
            Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95, skyDegrading: true), 8);
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95), s.ClearSafePolls);
            Assert.AreEqual(SafetyDecision.BecameSafe, d,
                "la camera concede il rientro nei poll previsti");
        }

        [TestMethod]
        public void Separation_DoesNotChangeTodaysTiming()
        {
            // Il §79 cambia la TARABILITA', non i tempi: alla consegna le due soglie
            // valgono lo stesso numero e il comportamento resta quello di prima.
            var e = new SafetyDecisionEngine();
            var s = new FakeSettings();
            Assert.AreEqual(s.CloudUnsafePolls, s.SkyDegradingUnsafePolls,
                "default allineati: nessun cambio di comportamento alla consegna");
            var d = Run(e, s, Snap(state: "CLEAR", fresh: true, index: 0.95,
                                   skyDegrading: true), s.SkyDegradingUnsafePolls);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
        }
}
}
