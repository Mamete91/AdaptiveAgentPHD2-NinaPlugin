#nullable enable
using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AdaptiveAgentForPHD2.NinaPlugin.Launch
{
    public enum LaunchLevel
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>Esito di un tentativo di avvio dell'Agente. Level guida il tipo di toast.</summary>
    public sealed record LaunchResult(bool Success, string Message, LaunchLevel Level)
    {
        public static LaunchResult Launched =>
            new(true, "Processo avviato. L'Agente sarà pronto tra qualche secondo.", LaunchLevel.Info);

        public static LaunchResult NotConfigured =>
            new(false, "Path al file Avvia.bat non impostato. Configuralo nelle settings del plugin.", LaunchLevel.Warning);

        public static LaunchResult FileNotFound =>
            new(false, "Il file Avvia.bat indicato nelle settings non esiste. Verifica il percorso.", LaunchLevel.Warning);

        public static LaunchResult Error(string message) =>
            new(false, $"Errore nell'avvio: {message}", LaunchLevel.Error);
    }

    /// <summary>
    /// Lancia il processo Avvia.bat dell'Agente. Non attende che l'Agente sia effettivamente
    /// raggiungibile: quella conferma arriva poi dal poller. Non propaga eccezioni.
    /// </summary>
    public sealed class AgentLauncher
    {
        public Task<LaunchResult> LaunchAsync(string? batPath)
        {
            if (string.IsNullOrWhiteSpace(batPath))
            {
                return Task.FromResult(LaunchResult.NotConfigured);
            }
            if (!File.Exists(batPath))
            {
                return Task.FromResult(LaunchResult.FileNotFound);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    WorkingDirectory = Path.GetDirectoryName(batPath) ?? "",
                    UseShellExecute = true,    // necessario per eseguire un .bat
                    CreateNoWindow = false,    // mostra la console: utile per il banner Python
                    WindowStyle = ProcessWindowStyle.Minimized,
                };
                Process.Start(psi);
                return Task.FromResult(LaunchResult.Launched);
            }
            catch (Exception ex)
            {
                Logger.Error($"Avvio Adaptive Agent fallito: {ex.Message}");
                return Task.FromResult(LaunchResult.Error(ex.Message));
            }
        }
    }
}
