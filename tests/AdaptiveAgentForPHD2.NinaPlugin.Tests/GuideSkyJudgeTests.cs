#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Health;
using AdaptiveAgentForPHD2.NinaPlugin.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// §126 — la guida giudica il cielo (decisione dell'operatore del 04/10/2026).
    /// Ogni test riproduce un caso reale del rapporto RAPPORTO_GUIDA_SOLA_2026-10-04.md
    /// o della forense della notte 3-4/10. Impostazioni = quelle reali del Minix
    /// (settings.json letto il 04/10: StarLost 180 s, ClearSafePolls 2, intervallo 15 s).
    /// </summary>
    [TestClass]
    public sealed class GuideSkyJudgeTests
    {
        private sealed class MinixSettings : ISafetySettings
        {
            public int HealthCheckIntervalSeconds { get; set; } = 15;
            public int StarLostConsolidationSeconds { get; set; } = 180;
            public bool CloudSafetyEnabled { get; set; } = true;
            public int CloudUnsafePolls { get; set; } = 8;
            public int ClearSafePolls { get; set; } = 2;
            public bool UseIndexCloudLogic { get; set; } = true;
            public double CloudIndexAccumulateBelow { get; set; } = 0.5;
            public double CloudIndexDrainAbove { get; set; } = 0.5;
            public bool StaleUnsafeEnabled { get; set; } = true;
            public int StaleUnsafePolls { get; set; } = 8;
            public bool AgentLostUnsafeEnabled { get; set; } = true;
            public int AgentLostUnsafePolls { get; set; } = 4;
            public bool GuideUnobservableEnabled { get; set; } = true;
            public int GuideSilenceSeconds { get; set; } = 90;
            public int GuideUnobservablePolls { get; set; } = 3;
            public bool SkyDegradingAccumulateEnabled { get; set; } = true;
            public int SkyDegradingUnsafePolls { get; set; } = 8;
            public bool ImagingCameraUnsafeEnabled { get; set; } = false;
        }

        /// <summary>Un tick di guida sana: stella tracciata, sereno confermato, canale pronto.</summary>
        private static AgentStatusSnapshot Guida(
            string guiding = "NORMAL", bool expected = true, bool? lost = false, double lostS = 0,
            bool tracked = true, bool degrading = false, bool skyOk = true, bool? ready = true,
            string? transpState = "CLEAR", bool fresh = true, double? index = 0.97,
            double frameAge = 1, bool reachable = true, bool valid = true, bool judgeReady = true)
            => new(guiding, valid, transpState, fresh, index, null, reachable,
                   GuideFrameAgeS: frameAge, GuidingExpected: expected,
                   SkyDegrading: degrading, StarLost: lost, StarLostS: lostS,
                   StarTracked: tracked, SkyOk: skyOk, ChannelReady: ready,
                   GuideJudgeReady: judgeReady);

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

        private static (SafetyDecisionEngine, MinixSettings) Unsafe_PerNubi()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Guida(degrading: true, skyOk: false), s.SkyDegradingUnsafePolls));
            return (e, s);
        }

        // ================================================================ il caso
        [TestMethod]
        public void Notte_3_4_Ottobre_PosaAllungata_NonFermaPiuLaNotte()
        {
            // 00:24:37 — indice 0.457 (525 stelle su 1143.6) per UNA posa con stelle
            // allungate, riletto a ogni poll. Con la camera giudice: UNSAFE alle 00:26:32
            // e tre ore di fermo. La stella di guida era a SNR 66.3 su 65.5.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            var d = Run(e, s, Guida(transpState: "HAZE", index: 0.457), 720);   // 3 ore
            Assert.AreEqual(SafetyDecision.NoChange, d, "la camera di ripresa e' informativa");
        }

        [TestMethod]
        public void OpzioneLegacy_LaCameraTornaGiudice()
        {
            // La controprova: con l'opzione accesa ritorna il comportamento fino al 1.13.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings { ImagingCameraUnsafeEnabled = true };
            var d = Run(e, s, Guida(transpState: "HAZE", index: 0.457), 8);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
            Assert.IsFalse(e.LastCloudFromGuide);
        }

        // ========================================================= cielo dalla guida
        [TestMethod]
        public void SegnaleDellaGuidaCrollato_Unsafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            for (int i = 0; i < s.SkyDegradingUnsafePolls - 1; i++)
            {
                Assert.AreEqual(SafetyDecision.NoChange, e.Evaluate(Guida(degrading: true, skyOk: false), s));
            }
            Assert.AreEqual(SafetyDecision.BecameUnsafe, e.Evaluate(Guida(degrading: true, skyOk: false), s));
            Assert.AreEqual(SafetyCause.Cloud, e.LastCause);
            Assert.IsTrue(e.LastCloudFromGuide);
        }

        [TestMethod]
        public void SerenoConfermatoDallaGuida_Safe_SenzaPosaDiVerifica()
        {
            var (e, s) = Unsafe_PerNubi();
            // tetto 8 / ClearSafePolls 2 = drena 4 per poll: due poll di sereno.
            Assert.AreEqual(SafetyDecision.NoChange, e.Evaluate(Guida(fresh: false, index: null), s));
            Assert.AreEqual(SafetyDecision.BecameSafe, e.Evaluate(Guida(fresh: false, index: null), s));
        }

        [TestMethod]
        public void FraLeDueSoglie_NonCambiaNiente()
        {
            // SNR fra il 50% e l'80% del sereno: ne' degrado ne' sereno.
            var (e, s) = Unsafe_PerNubi();
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(skyOk: false), 40));
        }

        [TestMethod]
        public void SerenoMaStellaInstabile_NonBasta()
        {
            // §71 — il riaggancio-lampo del 3/8 (41% di frame utili) non e' un sereno.
            var (e, s) = Unsafe_PerNubi();
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(ready: false), 40));
        }

        // ================================================================ fasi cieche
        [TestMethod]
        public void GuidaFerma_IlCieloSiCongela_InEntrambiIVersi()
        {
            // Calibrazione, Assistente di guida, autofocus: la guida non e' attesa.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Run(e, s, Guida(degrading: true, skyOk: false), 5);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(expected: false, degrading: true, skyOk: false), 60),
                "un degrado rimasto acceso non si conta a guida ferma (24/8)");

            var (e2, s2) = Unsafe_PerNubi();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e2, s2, Guida(expected: false), 60),
                "e nemmeno un sereno: a guida ferma non c'e' evidenza");
        }

        [TestMethod]
        public void GuideAssistant_TestDelBacklash_NonEUnCanaleMuto()
        {
            // 2/10 23:13:42 — durante il GA l'Agente non segnava i frame; nel test del
            // backlash PHD2 non ne manda per ~2 minuti. Con l'Agente 3.1 la guida in quella
            // fase non e' attesa: nessun GUIDE_UNOBSERVABLE.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(expected: false, frameAge: 300, tracked: false), 30));
        }

        // ================================================================ stella persa
        [TestMethod]
        public void UnFramePerso_NonRestaAppiccicato()
        {
            // 10/8 — un frame perso alle 22:32:03, poi 126 buoni, e UNSAFE alle 22:34:55:
            // lo stato del controller restava STAR_LOST con l'RMS nella banda neutra.
            // Il ramo dagli eventi legge la stella, non lo stato del controller.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            e.Evaluate(Guida(guiding: "STAR_LOST", lost: true, lostS: 3, tracked: false), s);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(guiding: "STAR_LOST"), 60),
                "lo stato vecchio del controller non deve piu' contare");
        }

        [TestMethod]
        public void StellaPersaPer180s_Unsafe()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            var d = SafetyDecision.NoChange;
            for (int i = 1; i <= 11; i++)
            {
                d = e.Evaluate(Guida(lost: true, lostS: 15.0 * i, tracked: false, skyOk: false), s);
                Assert.AreEqual(SafetyDecision.NoChange, d, $"a {15 * i} s e' presto");
            }
            d = e.Evaluate(Guida(lost: true, lostS: 180, tracked: false, skyOk: false), s);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.StarLost, e.LastCause);
        }

        [TestMethod]
        public void StellaRitrovata_IlSafeAspettaCheLaGuidaConfermiIlCielo()
        {
            // 3/8 — nubi a tratti: la stella tornava con SNR al 28-49% del riferimento.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Run(e, s, Guida(lost: true, lostS: 200, tracked: false, skyOk: false), 1);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(skyOk: false), 30),
                "stella ritrovata ma cielo non ancora sereno: resta UNSAFE");
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(), 3));
        }

        [TestMethod]
        public void StellaPersaAGuidaFerma_NonSiCongela()
        {
            // Fail-safe: guida fermata mentre la stella e' persa, nessuno la rivede.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            var d = e.Evaluate(Guida(expected: false, lost: true, lostS: 240, tracked: false, skyOk: false), s);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.AreEqual(SafetyCause.StarLost, e.LastCause);
        }

        // ================================================================ agente perso
        [TestMethod]
        public void AgentePerso_AlRitornoDecideLaGuida()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            e.Evaluate(Guida(), s);                                       // sessione attiva
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, new AgentStatusSnapshot(null, false, AgentReachable: false), 4));
            Assert.AreEqual(SafetyCause.AgentLost, e.LastCause);
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(skyOk: false), 20),
                "l'Agente risponde, ma il cielo non e' ancora confermato");
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(), 2));
        }

        // ============================================== percorsi della camera spenti
        [TestMethod]
        public void TelemetriaStantia_NonEsistePiuConLaGuidaGiudice()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            e.Evaluate(Guida(transpState: "CLOUD", index: 0.08), s);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(transpState: "CLOUD", fresh: false, index: null), 40));
        }

        [TestMethod]
        public void SicurezzaNubiSpenta_RestanoStellaPersaECanaleMuto()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings { CloudSafetyEnabled = false };
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(degrading: true, skyOk: false), 40));
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                e.Evaluate(Guida(lost: true, lostS: 180, tracked: false), s));
            // senza il percorso del cielo, la stella ritrovata basta (3 tick).
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(skyOk: false), 3));
        }

        // ============================================== §126-bis: seconda tornata
        [TestMethod]
        public void AgenteSenzaGiudice_DecideLaCamera_ComeIl113()
        {
            // Plugin 1.14 con un Agente 3.0 (o sensore spento): niente giudice guida,
            // che chiuderebbe in uno stallo; si torna al comportamento provato.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            var d = Run(e, s, Guida(transpState: "HAZE", index: 0.457, judgeReady: false), 8);
            Assert.IsFalse(e.GuideJudgeActive);
            Assert.AreEqual(SafetyDecision.BecameUnsafe, d);
            Assert.IsFalse(e.LastCloudFromGuide);
        }

        [TestMethod]
        public void CambioDiGiudiceALatchAcceso_NonSpegneGratis()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings { ImagingCameraUnsafeEnabled = true };
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Guida(transpState: "CLOUD", index: 0.1), 8));
            s.ImagingCameraUnsafeEnabled = false;                 // l'operatore cambia opzione
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(skyOk: false), 20),
                "il latch passa al cielo della guida, non si spegne");
            Assert.IsTrue(e.GuideJudgeActive);
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(), 2));
        }

        [TestMethod]
        public void CanaleMuto_RientraSoloConIlSereno()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Guida(frameAge: 120, tracked: false, skyOk: false), 3));
            Assert.AreEqual(SafetyCause.GuideUnobservable, e.LastCause);
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(skyOk: false, ready: false), 20),
                "i frame tornano, ma il sereno non e' ancora confermato");
            Assert.AreEqual(SafetyCause.Cloud, e.CurrentCause);
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(), 2));
        }

        [TestMethod]
        public void CanaleMuto_PoiGuidaFermata_NonSpegneGratis()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            Run(e, s, Guida(frameAge: 120, tracked: false, skyOk: false), 3);
            Assert.AreEqual(SafetyDecision.NoChange,
                Run(e, s, Guida(expected: false, tracked: false, skyOk: false), 20));
        }

        [TestMethod]
        public void CausaAttuale_DopoLaStellaRitrovata_EIlCielo()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            e.Evaluate(Guida(lost: true, lostS: 200, tracked: false, skyOk: false), s);
            Assert.AreEqual(SafetyCause.StarLost, e.CurrentCause);
            Run(e, s, Guida(skyOk: false), 3);
            Assert.AreEqual(SafetyCause.Cloud, e.CurrentCause, "si aspetta il sereno, la stella c'e'");
        }

        [TestMethod]
        public void ConLaGuidaGiudice_LaCasellaDellAnticipoNonSpegneLeNubi()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings { SkyDegradingAccumulateEnabled = false };
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Guida(degrading: true, skyOk: false), 8));
        }

        [TestMethod]
        public void SicurezzaNubiSpentaALatchAcceso_IlLatchSiSpegne()
        {
            var (e, s) = Unsafe_PerNubi();
            s.CloudSafetyEnabled = false;
            Assert.AreEqual(SafetyDecision.BecameSafe, e.Evaluate(Guida(skyOk: false), s));
        }

        [TestMethod]
        public void CambioDiGiudice_AncheLaTelemetriaFermaSiTrasferisce()
        {
            // §126-quater (S7): con la camera giudice un latch STALE acceso; l'operatore
            // passa alla guida: il latch non deve spegnersi senza evidenza.
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings { ImagingCameraUnsafeEnabled = true };
            e.Evaluate(Guida(transpState: "CLOUD", index: 0.08), s);
            Assert.AreEqual(SafetyDecision.BecameUnsafe,
                Run(e, s, Guida(transpState: "CLOUD", fresh: false, index: null, skyOk: false), 8));
            s.ImagingCameraUnsafeEnabled = false;
            Assert.AreEqual(SafetyDecision.NoChange, Run(e, s, Guida(skyOk: false), 10));
            Assert.AreEqual(SafetyDecision.BecameSafe, Run(e, s, Guida(), 2));
        }

        [TestMethod]
        public void Reset_RidecideIlGiudice()
        {
            var e = new SafetyDecisionEngine();
            var s = new MinixSettings();
            e.Evaluate(Guida(), s);
            Assert.IsTrue(e.JudgeDecided);
            e.Reset();
            Assert.IsFalse(e.JudgeDecided);
        }
    }
}
