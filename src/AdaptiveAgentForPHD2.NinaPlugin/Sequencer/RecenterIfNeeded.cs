#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Lifecycle;
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>
    /// §129 — "Recenter if needed (Adaptive Agent)": da mettere in Trigger On Unsafe →
    /// After Waiting For Safety (contenitore che NINA 3.3 esegue al ritorno del safe,
    /// annullato e riavviato se torna unsafe).
    ///
    /// Decisione dell'operatore (21/11/2026): ricentrare solo quando serve davvero — dopo un
    /// flip avvenuto in unsafe (il ricentraggio di NINA sotto le nubi fallisce in silenzio)
    /// o dopo almeno N minuti senza guida (deriva). Il "serve davvero" non si indovina: si
    /// MISURA con un solo plate solve, e si ricentra solo se lo scarto supera la tolleranza
    /// di centraggio del profilo NINA. Nessuno studio della deriva a priori: ogni controllo
    /// scrive nel log minuti senza guida e scarto misurato, per tarare N con i dati veri.
    ///
    /// Fail-inert: qualunque problema (montatura, camera, plate solve) => log e la sequenza
    /// prosegue come senza l'istruzione. Mai un IsSafe toccato: l'istruzione non giudica il
    /// cielo, sa solo dove punta il telescopio.
    ///
    /// API di NINA usate (verificate uguali fra SDK 3.2.0.9001 e NINA 3.3.0.1070, commit
    /// db6b9090acdf): IPlateSolverFactory.GetPlateSolver/GetBlindSolver/GetCaptureSolver/
    /// GetCenteringSolver, ICaptureSolver.Solve, ICenteringSolver.Center, CaptureSequence,
    /// CaptureSolverParameter/CenterSolveParameter, Coordinates - Coordinates, ItemUtility.
    /// RetrieveContextCoordinates, IGuiderMediator.StopGuiding/StartGuiding.
    /// </summary>
    [ExportMetadata("Name", "Recenter if needed (Adaptive Agent)")]
    [ExportMetadata("Description",
        "Put it in Trigger On Unsafe > After Waiting For Safety. When the sky is clear again it checks the " +
        "framing with one plate solve, only if a meridian flip ran while unsafe or guiding was lost or stopped " +
        "for at least the set minutes; it recenters only if the offset exceeds the N.I.N.A. centering tolerance.\n\n" +
        "IT: mettila in Trigger On Unsafe > After Waiting For Safety. Al ritorno del sereno controlla " +
        "l'inquadratura con un solo plate solve, solo se durante l'unsafe c'e' stato un meridian flip o la guida " +
        "e' rimasta persa o ferma almeno i minuti impostati; ricentra solo se lo scarto supera la tolleranza di " +
        "centraggio di N.I.N.A.")]
    [ExportMetadata("Icon", "PlatesolveSVG")]
    [ExportMetadata("Category", "Adaptive Agent for PHD2")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RecenterIfNeeded : SequenceItem, IValidatable
    {
        public const double DefaultUnguidedMinutes = 12;

        private readonly IProfileService _profile;
        private readonly ITelescopeMediator _telescope;
        private readonly IImagingMediator _imaging;
        private readonly IFilterWheelMediator _filterWheel;
        private readonly IGuiderMediator _guider;
        private readonly IDomeMediator _dome;
        private readonly IDomeFollower _domeFollower;
        private readonly IPlateSolverFactory _solverFactory;
        private readonly ICameraMediator _camera;

        private double _unguidedMinutes = DefaultUnguidedMinutes;
        private IList<string> _issues = new List<string>();

        [ImportingConstructor]
        public RecenterIfNeeded(IProfileService profileService,
                                ITelescopeMediator telescopeMediator,
                                IImagingMediator imagingMediator,
                                IFilterWheelMediator filterWheelMediator,
                                IGuiderMediator guiderMediator,
                                IDomeMediator domeMediator,
                                IDomeFollower domeFollower,
                                IPlateSolverFactory plateSolverFactory,
                                ICameraMediator cameraMediator)
        {
            _profile = profileService;
            _telescope = telescopeMediator;
            _imaging = imagingMediator;
            _filterWheel = filterWheelMediator;
            _guider = guiderMediator;
            _dome = domeMediator;
            _domeFollower = domeFollower;
            _solverFactory = plateSolverFactory;
            _camera = cameraMediator;
        }

        private RecenterIfNeeded(RecenterIfNeeded cloneMe)
            : this(cloneMe._profile, cloneMe._telescope, cloneMe._imaging, cloneMe._filterWheel,
                   cloneMe._guider, cloneMe._dome, cloneMe._domeFollower, cloneMe._solverFactory, cloneMe._camera)
        {
            CopyMetaData(cloneMe);
        }

        public override object Clone() => new RecenterIfNeeded(this) { UnguidedMinutes = UnguidedMinutes };

        /// <summary>Minuti senza guida, sommati durante l'unsafe, oltre i quali al ritorno del
        /// sereno si controlla la posizione. Un flip durante l'unsafe porta sempre al controllo.</summary>
        [JsonProperty]
        public double UnguidedMinutes
        {
            get => _unguidedMinutes;
            set { _unguidedMinutes = Math.Clamp(value, 1, 240); RaisePropertyChanged(); }
        }

        public IList<string> Issues
        {
            get => _issues;
            private set { _issues = value; RaisePropertyChanged(); }
        }

        private RecenterTracker? _tracker;

        /// <summary>La memoria condivisa alimentata dal monitor (non salvata nella sequenza).
        /// Impostabile solo per i test.</summary>
        public RecenterTracker Tracker
        {
            get => _tracker ?? AgentServices.Instance.RecenterTracker;
            set => _tracker = value;
        }

        public bool Validate()
        {
            var issues = new List<string>();
            try
            {
                if (!_telescope.GetInfo().Connected) { issues.Add(Loc.T("Recenter_Issue_Telescope")); }
                if (!_camera.GetInfo().Connected) { issues.Add(Loc.T("Recenter_Issue_Camera")); }
            }
            catch
            {
                // La validazione non deve mai rompere l'editor della sequenza.
            }
            Issues = issues;
            return issues.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            var tracker = Tracker;
            var decision = tracker.Decide(UnguidedMinutes);
            if (!decision.Check)
            {
                Logger.Info($"Recenter if needed: no position check — {decision.Reason}");
                return;
            }

            bool guidingStopped = false;
            try
            {
                var target = ResolveTarget(tracker);
                if (target == null)
                {
                    Logger.Warning("Recenter if needed: no target coordinates (put the Trigger On Unsafe inside "
                                   + "the target container) — position check skipped");
                    return;
                }
                var tel = _telescope.GetInfo();
                if (!tel.Connected || tel.AtPark || !tel.TrackingEnabled || tel.Slewing)
                {
                    Logger.Warning("Recenter if needed: mount not ready (connected=" + tel.Connected + ", parked="
                                   + tel.AtPark + ", tracking=" + tel.TrackingEnabled + ", slewing=" + tel.Slewing
                                   + ") — position check skipped");
                    return;
                }
                if (!_camera.GetInfo().Connected)
                {
                    Logger.Warning("Recenter if needed: camera not connected — position check skipped");
                    return;
                }

                var profile = _profile.ActiveProfile;
                var ps = profile.PlateSolveSettings;
                double tolerance = ps.Threshold;
                var plateSolver = _solverFactory.GetPlateSolver(ps);
                var blindSolver = _solverFactory.GetBlindSolver(ps);
                var seq = new CaptureSequence(ps.ExposureTime, "SNAPSHOT", ps.Filter,
                                              new BinningMode(ps.Binning, ps.Binning), 1) { Gain = ps.Gain };

                // 1) Controllo: UN plate solve (al massimo due tentativi, non i tentativi del
                // profilo: se il cielo si richiude non si resta fermi minuti), guida accesa,
                // nessun movimento della montatura.
                Logger.Info($"Recenter if needed: checking the framing — {decision.Reason}");
                progress?.Report(new ApplicationStatus { Status = "Recenter if needed: checking the framing" });
                var check = new CaptureSolverParameter
                {
                    Attempts = Math.Max(1, Math.Min(2, ps.NumberOfAttempts)),
                    ReattemptDelay = TimeSpan.FromMinutes(ps.ReattemptDelay),
                    Binning = ps.Binning,
                    Coordinates = target,
                    DownSampleFactor = ps.DownSampleFactor,
                    FocalLength = profile.TelescopeSettings.FocalLength,
                    MaxObjects = ps.MaxObjects,
                    PixelSize = profile.CameraSettings.PixelSize,
                    Regions = ps.Regions,
                    SearchRadius = ps.SearchRadius,
                    BlindFailoverEnabled = ps.BlindFailoverEnabled,
                };
                var solved = await _solverFactory.GetCaptureSolver(plateSolver, blindSolver, _imaging, _filterWheel)
                    .Solve(seq, check, null, progress, token).ConfigureAwait(false);
                if (solved == null || !solved.Success || solved.Coordinates == null)
                {
                    // Niente consumato: il motivo resta per il prossimo ritorno del sereno.
                    Logger.Warning("Recenter if needed: the check plate solve failed (clouds back, or plate solving "
                                   + "not set up) — framing not verified, will retry at the next return to safe");
                    return;
                }

                var position = _telescope.GetCurrentPosition();
                var epoch = position?.Epoch ?? target.Epoch;
                var separation = target.Transform(epoch) - solved.Coordinates.Transform(epoch);
                double offset = Math.Abs(separation.Distance.ArcMinutes);
                string summary = $"{decision.UnguidedMinutes:0.#} min without guiding while unsafe"
                                 + (decision.FlipDuringUnsafe ? ", meridian flip while unsafe" : "")
                                 + $", offset {offset:0.00}' (tolerance {tolerance:0.##}')";
                if (offset <= tolerance)
                {
                    Logger.Info($"Recenter if needed: framing within tolerance — {summary}; nothing to do");
                    tracker.Consume();
                    return;
                }

                // 2) Fuori tolleranza: come l'istruzione Center di NINA — guida ferma, centraggio
                // con le impostazioni del profilo, guida di nuovo avviata.
                Logger.Warning($"Recenter if needed: framing OFF — {summary}; recentering");
                progress?.Report(new ApplicationStatus { Status = "Recenter if needed: recentering the target" });
                guidingStopped = await _guider.StopGuiding(token).ConfigureAwait(false);
                var center = new CenterSolveParameter
                {
                    Attempts = Math.Max(1, ps.NumberOfAttempts),
                    ReattemptDelay = TimeSpan.FromMinutes(ps.ReattemptDelay),
                    Binning = ps.Binning,
                    Coordinates = target,
                    DownSampleFactor = ps.DownSampleFactor,
                    FocalLength = profile.TelescopeSettings.FocalLength,
                    MaxObjects = ps.MaxObjects,
                    PixelSize = profile.CameraSettings.PixelSize,
                    Regions = ps.Regions,
                    SearchRadius = ps.SearchRadius,
                    Threshold = tolerance,
                    NoSync = profile.TelescopeSettings.NoSync,
                    BlindFailoverEnabled = ps.BlindFailoverEnabled,
                };
                var centered = await _solverFactory
                    .GetCenteringSolver(plateSolver, blindSolver, _imaging, _telescope, _filterWheel, _dome, _domeFollower)
                    .Center(seq, center, null, progress, token).ConfigureAwait(false);
                if (centered != null && centered.Success)
                {
                    Logger.Info($"Recenter if needed: target recentered — {summary}");
                    tracker.Consume();
                    ToastHelper.Show(() => NINA.Core.Utility.Notification.Notification.ShowInformation(
                        string.Format(Loc.T("Toast_Recentered"), offset.ToString("0.0", CultureInfo.CurrentCulture))));
                }
                else
                {
                    Logger.Warning($"Recenter if needed: recentering FAILED — {summary}; the sequence continues");
                    ToastHelper.Show(() => NINA.Core.Utility.Notification.Notification.ShowWarning(
                        string.Format(Loc.T("Toast_RecenterFailed"), offset.ToString("0.0", CultureInfo.CurrentCulture))));
                }
            }
            catch (OperationCanceledException)
            {
                // Annullata (sequenza fermata, o NINA ha riavviato il trigger perche' e' tornato
                // unsafe): il motivo resta per il prossimo ritorno del sereno.
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Recenter if needed: position check failed ({ex.Message}) — the sequence continues");
            }
            finally
            {
                // Annullata (sequenza fermata, o unsafe tornato durante il centraggio): niente
                // ripartenza qui, che terrebbe ferma la cancellazione per minuti; se e' tornato
                // l'unsafe la guida la fa ripartire la Recovery probe.
                if (guidingStopped && !token.IsCancellationRequested)
                {
                    try
                    {
                        await _guider.StartGuiding(false, progress, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Recenter if needed: restarting guiding after the recenter failed ({ex.Message})");
                    }
                }
            }
        }

        /// <summary>Le coordinate del contenitore dell'oggetto (come l'istruzione Center con
        /// coordinate ereditate); se mancano, quelle dell'ultimo flip di NINA.</summary>
        private Coordinates? ResolveTarget(RecenterTracker tracker)
        {
            try
            {
                var context = ItemUtility.RetrieveContextCoordinates(Parent);
                if (context?.Coordinates is Coordinates c && !(c.RA == 0 && c.Dec == 0)) { return c; }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Recenter if needed: context coordinates unavailable ({ex.Message})");
            }
            return tracker.LastFlipTarget;
        }

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(30);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(RecenterIfNeeded)}, UnguidedMinutes: {UnguidedMinutes}";
    }
}
