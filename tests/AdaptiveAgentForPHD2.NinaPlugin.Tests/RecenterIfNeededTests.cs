#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Sequencer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NINA.Astrometry;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Tests
{
    /// <summary>
    /// Finto generico per le interfacce di NINA (DispatchProxy, incluso in .NET: nessun
    /// pacchetto in piu'). Registra le chiamate; per ogni metodo si puo' dare una risposta,
    /// altrimenti restituisce il valore di default del tipo (Task completati compresi).
    /// </summary>
    public class Finto : DispatchProxy
    {
        public Dictionary<string, Func<object?[]?, object?>> Risposte { get; } = new();
        public List<(string Nome, object?[]? Argomenti)> Chiamate { get; } = new();

        public int Quante(string nome) => Chiamate.Count(c => c.Nome == nome);

        protected override object? Invoke(MethodInfo? metodo, object?[]? args)
        {
            var nome = metodo!.Name;
            Chiamate.Add((nome, args));
            if (Risposte.TryGetValue(nome, out var r)) { return r(args); }
            var tipo = metodo.ReturnType;
            if (tipo == typeof(void)) { return null; }
            if (tipo == typeof(Task)) { return Task.CompletedTask; }
            if (tipo.IsGenericType && tipo.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var interno = tipo.GetGenericArguments()[0];
                var valore = interno.IsValueType ? Activator.CreateInstance(interno) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(interno)
                                   .Invoke(null, new[] { valore });
            }
            return tipo.IsValueType ? Activator.CreateInstance(tipo) : null;
        }

        public static T Crea<T>(out Finto finto) where T : class
        {
            var o = Create<T, Finto>();
            finto = (Finto)(object)o;
            return o;
        }
    }

    /// <summary>
    /// §129 — "Recenter if needed": cosa fa davvero con montatura, camera, guida e plate solve
    /// (finti). Profilo come quello del Minix: plate solve 30 tentativi, tolleranza 2'.
    /// </summary>
    [TestClass]
    public sealed class RecenterIfNeededTests
    {
        private static readonly Coordinates Bersaglio =
            new(Angle.ByHours(23.35), Angle.ByDegree(61.2), Epoch.J2000);

        private sealed class Banco
        {
            public RecenterIfNeeded Istruzione = null!;
            public RecenterTracker Memoria = new();
            public Finto Guida = null!, Camera = null!, Fabbrica = null!, Controllo = null!, Centraggio = null!;
            public TelescopeInfo Montatura = new() { Connected = true, TrackingEnabled = true };
            public PlateSolveResult EsitoControllo = new() { Success = true, Coordinates = Bersaglio };
            public PlateSolveResult EsitoCentraggio = new() { Success = true };
            public CaptureSolverParameter? ParametriControllo;
            public CenterSolveParameter? ParametriCentraggio;
        }

        private static Banco Prepara()
        {
            var b = new Banco();
            var ps = Finto.Crea<IPlateSolveSettings>(out var fps);
            fps.Risposte["get_ExposureTime"] = _ => 10.0;
            fps.Risposte["get_Binning"] = _ => (short)1;
            fps.Risposte["get_NumberOfAttempts"] = _ => 30;
            fps.Risposte["get_ReattemptDelay"] = _ => 0.1;
            fps.Risposte["get_Threshold"] = _ => 2.0;
            fps.Risposte["get_BlindFailoverEnabled"] = _ => true;
            var tel = Finto.Crea<ITelescopeSettings>(out var ftel);
            ftel.Risposte["get_FocalLength"] = _ => 1624.0;
            var cam = Finto.Crea<ICameraSettings>(out var fcam);
            fcam.Risposte["get_PixelSize"] = _ => 3.76;
            var profilo = Finto.Crea<IProfile>(out var fprof);
            fprof.Risposte["get_PlateSolveSettings"] = _ => ps;
            fprof.Risposte["get_TelescopeSettings"] = _ => tel;
            fprof.Risposte["get_CameraSettings"] = _ => cam;
            var servizio = Finto.Crea<IProfileService>(out var fsrv);
            fsrv.Risposte["get_ActiveProfile"] = _ => profilo;

            var montatura = Finto.Crea<ITelescopeMediator>(out var fmont);
            fmont.Risposte["GetInfo"] = _ => b.Montatura;
            fmont.Risposte["GetCurrentPosition"] = _ => Bersaglio;
            var camera = Finto.Crea<ICameraMediator>(out b.Camera);
            b.Camera.Risposte["GetInfo"] = _ => new CameraInfo { Connected = true };
            var guida = Finto.Crea<IGuiderMediator>(out b.Guida);
            b.Guida.Risposte["StopGuiding"] = _ => Task.FromResult(true);
            b.Guida.Risposte["StartGuiding"] = _ => Task.FromResult(true);

            var controllo = Finto.Crea<ICaptureSolver>(out b.Controllo);
            b.Controllo.Risposte["Solve"] = a =>
            {
                b.ParametriControllo = (CaptureSolverParameter)a![1]!;
                return Task.FromResult(b.EsitoControllo);
            };
            var centraggio = Finto.Crea<ICenteringSolver>(out b.Centraggio);
            b.Centraggio.Risposte["Center"] = a =>
            {
                b.ParametriCentraggio = (CenterSolveParameter)a![1]!;
                return Task.FromResult(b.EsitoCentraggio);
            };
            var fabbrica = Finto.Crea<IPlateSolverFactory>(out b.Fabbrica);
            b.Fabbrica.Risposte["GetCaptureSolver"] = _ => controllo;
            b.Fabbrica.Risposte["GetCenteringSolver"] = _ => centraggio;

            b.Istruzione = new RecenterIfNeeded(servizio, montatura,
                Finto.Crea<IImagingMediator>(out _), Finto.Crea<IFilterWheelMediator>(out _), guida,
                Finto.Crea<IDomeMediator>(out _), Finto.Crea<IDomeFollower>(out _), fabbrica, camera)
            { Tracker = b.Memoria };
            b.Memoria.Reset(monitorConnected: true);
            b.Memoria.OnTick(true, false, 0, 45);
            return b;
        }

        /// <summary>Un flip passato sotto le nubi, poi il ritorno del sereno.</summary>
        private static void FlipSottoLeNubi(Banco b)
        {
            b.Memoria.OnTick(false, false, 15, 45);
            b.Memoria.OnFlipStarted(internalSafe: false, Bersaglio);
            b.Memoria.OnFlipFinished(internalSafe: false);
            b.Memoria.OnTick(true, false, 30, 45);
        }

        private static Coordinates Spostato(double primiInDeclinazione) =>
            new(Angle.ByHours(23.35), Angle.ByDegree(61.2 + primiInDeclinazione / 60.0), Epoch.J2000);

        private static void Esegui(Banco b) =>
            b.Istruzione.Execute(null!, CancellationToken.None).GetAwaiter().GetResult();

        [TestMethod]
        public void NessunMotivo_NessunaFoto()
        {
            var b = Prepara();
            Esegui(b);
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCaptureSolver"), "nessun plate solve senza motivo");
            Assert.AreEqual(0, b.Guida.Quante("StopGuiding"));
        }

        [TestMethod]
        public void FlipSottoLeNubi_RicentraggioDiNinaRiuscito_UnaFotoENienteAltro()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.EsitoControllo = new PlateSolveResult { Success = true, Coordinates = Spostato(0.5) };
            Esegui(b);
            Assert.AreEqual(1, b.Controllo.Quante("Solve"));
            Assert.AreEqual(0, b.Guida.Quante("StopGuiding"), "la guida non si tocca se l'oggetto e' in tolleranza");
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCenteringSolver"));
            Assert.IsFalse(b.Memoria.Decide(12).Check, "controllo riuscito: motivo chiuso");
            Assert.AreEqual(2, b.ParametriControllo!.Attempts, "il controllo non usa i 30 tentativi del profilo");
            Assert.IsNotNull(b.ParametriControllo.Coordinates);
        }

        [TestMethod]
        public void FlipSottoLeNubi_RicentraggioDiNinaFallito_Ricentra()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.EsitoControllo = new PlateSolveResult { Success = true, Coordinates = Spostato(6.0) };
            Esegui(b);
            Assert.AreEqual(1, b.Guida.Quante("StopGuiding"));
            Assert.AreEqual(1, b.Centraggio.Quante("Center"));
            Assert.AreEqual(1, b.Guida.Quante("StartGuiding"), "la guida riparte dopo il ricentraggio");
            Assert.AreEqual(30, b.ParametriCentraggio!.Attempts, "il centraggio usa le impostazioni del profilo");
            Assert.AreEqual(2.0, b.ParametriCentraggio.Threshold, 1e-9);
            Assert.IsFalse(b.Memoria.Decide(12).Check);
        }

        [TestMethod]
        public void PlateSolveDiControlloFallito_NienteMossoEMotivoConservato()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.EsitoControllo = new PlateSolveResult { Success = false };
            Esegui(b);
            Assert.AreEqual(0, b.Guida.Quante("StopGuiding"));
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCenteringSolver"));
            Assert.IsTrue(b.Memoria.Decide(12).Check, "si riprova al prossimo ritorno del sereno");
        }

        [TestMethod]
        public void CentraggioFallito_LaGuidaRipartecomunque_EMotivoConservato()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.EsitoControllo = new PlateSolveResult { Success = true, Coordinates = Spostato(6.0) };
            b.EsitoCentraggio = new PlateSolveResult { Success = false };
            Esegui(b);
            Assert.AreEqual(1, b.Guida.Quante("StartGuiding"));
            Assert.IsTrue(b.Memoria.Decide(12).Check);
        }

        [TestMethod]
        public void CentraggioCheLanciaUnErrore_LaGuidaRipartecomunque_ELaSequenzaProsegue()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.EsitoControllo = new PlateSolveResult { Success = true, Coordinates = Spostato(6.0) };
            b.Centraggio.Risposte["Center"] = _ => throw new InvalidOperationException("plate solver crashed");
            Esegui(b);   // nessuna eccezione verso NINA
            Assert.AreEqual(1, b.Guida.Quante("StartGuiding"));
        }

        [TestMethod]
        public void MontaturaParcheggiata_NessunaFoto()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.Montatura.AtPark = true;
            Esegui(b);
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCaptureSolver"));
            Assert.IsTrue(b.Memoria.Decide(12).Check);
        }

        [TestMethod]
        public void TrackingFermo_NessunaFoto()
        {
            var b = Prepara();
            FlipSottoLeNubi(b);
            b.Montatura.TrackingEnabled = false;
            Esegui(b);
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCaptureSolver"));
        }

        [TestMethod]
        public void SenzaCoordinate_NessunaFoto()
        {
            // Nessun contenitore dell'oggetto e nessun flip con bersaglio: non si sa dove centrare.
            var b = Prepara();
            b.Memoria.OnTick(false, true, 15, 45);
            for (int i = 2; i < 60; i++) { b.Memoria.OnTick(false, true, 15 * i, 45); }
            b.Memoria.OnTick(true, false, 15 * 61, 45);
            Assert.IsTrue(b.Memoria.Decide(12).Check);
            Esegui(b);
            Assert.AreEqual(0, b.Fabbrica.Quante("GetCaptureSolver"));
        }

        [TestMethod]
        public void MinutiSenzaGuida_SiSalvanoNellaSequenza()
        {
            var b = Prepara();
            b.Istruzione.UnguidedMinutes = 20;
            var copia = (RecenterIfNeeded)b.Istruzione.Clone();
            Assert.AreEqual(20, copia.UnguidedMinutes);
            b.Istruzione.UnguidedMinutes = 0;
            Assert.AreEqual(1, b.Istruzione.UnguidedMinutes, "mai sotto 1 minuto");
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(copia);
            StringAssert.Contains(json, "\"UnguidedMinutes\":20");
            Assert.IsFalse(json.Contains("Tracker"), "la memoria condivisa non va nella sequenza");
        }
    }
}
