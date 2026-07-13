#nullable enable
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Mediator;
using System;

namespace AdaptiveAgentForPHD2.NinaPlugin.Sequencer
{
    /// <summary>Profilo del light interrotto: i parametri che la posa-sonda replica (§57-bis).</summary>
    public sealed record LastLightProfile(
        double ExposureSeconds,
        int Gain,
        int Offset,
        short BinX,
        short BinY,
        string? Filter,
        DateTimeOffset SavedAtUtc);

    /// <summary>
    /// §57-bis — memoria condivisa dell'ultimo LIGHT salvato da NINA. La RecoveryProbe
    /// la legge per produrre una sonda EQUIVALENTE al sub che la sequenza avrebbe
    /// acquisito (stessa esposizione/gain/offset/binning; il filtro non serve:
    /// la ruota e' rimasta in posizione). Thread-safe, solo memoria: nessuna cattura qui.
    /// </summary>
    public static class LastLightMemory
    {
        private static readonly object _lock = new();
        private static LastLightProfile? _current;

        public static LastLightProfile? Current
        {
            get { lock (_lock) { return _current; } }
        }

        public static void Update(LastLightProfile profile)
        {
            lock (_lock) { _current = profile; }
        }

        public static void Clear()
        {
            lock (_lock) { _current = null; }
        }
    }

    /// <summary>
    /// Subscriber di ImageSaved che alimenta LastLightMemory (solo frame LIGHT).
    /// Posseduto dal Plugin (stesso ciclo di vita del forwarder §42): il forwarder
    /// telemetria resta INVARIATO — responsabilita' separate.
    /// </summary>
    public sealed class LastLightTracker : IDisposable
    {
        private readonly IImageSaveMediator _imageSaveMediator;
        private bool _subscribed;
        private bool _disposed;

        public LastLightTracker(IImageSaveMediator imageSaveMediator)
        {
            _imageSaveMediator = imageSaveMediator;
        }

        public void Subscribe()
        {
            if (_subscribed || _disposed || _imageSaveMediator == null) { return; }
            _imageSaveMediator.ImageSaved += OnImageSaved;
            _subscribed = true;
        }

        public void Unsubscribe()
        {
            if (!_subscribed || _imageSaveMediator == null) { return; }
            _imageSaveMediator.ImageSaved -= OnImageSaved;
            _subscribed = false;
        }

        // Handler veloce e non-throwing (stesso patto del forwarder §42).
        private void OnImageSaved(object? sender, ImageSavedEventArgs e)
        {
            try
            {
                if (e?.MetaData == null) { return; }
                // Solo LIGHT: snapshot/flat/dark non descrivono il sub interrotto.
                var imageType = e.MetaData.Image?.ImageType;
                if (!string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) { return; }
                if (!(e.Duration > 0)) { return; }

                var cam = e.MetaData.Camera;
                LastLightMemory.Update(new LastLightProfile(
                    ExposureSeconds: e.Duration,
                    Gain: cam?.Gain ?? -1,
                    Offset: cam?.Offset ?? -1,
                    BinX: (short)(cam?.BinX ?? 1),
                    BinY: (short)(cam?.BinY ?? 1),
                    Filter: e.Filter,
                    SavedAtUtc: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                Logger.Debug($"LastLightTracker: ImageSaved handler ignored ({ex.Message})");
            }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Unsubscribe();
        }
    }
}
