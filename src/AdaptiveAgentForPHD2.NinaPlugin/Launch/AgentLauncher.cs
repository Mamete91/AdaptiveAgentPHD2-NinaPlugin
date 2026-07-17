#nullable enable
using AdaptiveAgentForPHD2.NinaPlugin.Localization;
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
            new(true, Loc.T("Launch_Started"), LaunchLevel.Info);

        public static LaunchResult NotConfigured =>
            new(false, Loc.T("Launch_NotConfigured"), LaunchLevel.Warning);

        public static LaunchResult FileNotFound =>
            new(false, Loc.T("Launch_FileNotFound"), LaunchLevel.Warning);

        public static LaunchResult Error(string message) =>
            new(false, string.Format(Loc.T("Launch_Error"), message), LaunchLevel.Error);
    }

    /// <summary>
    /// Lancia il processo Avvia.bat dell'Agente. Non attende che l'Agente sia effettivamente
    /// raggiungibile: quella conferma arriva poi dal poller. Non propaga eccezioni.
    ///
    /// §58 — conserva l'handle del processo avviato (LastStartedProcess). ATTENZIONE:
    /// lanciando un .bat l'handle e' cmd.exe e il python dell'Agente e' un PROCESSO
    /// FIGLIO → un eventuale kill di fallback deve uccidere l'ALBERO
    /// (Process.Kill(entireProcessTree: true)), mai il solo handle.
    /// </summary>
    public sealed class AgentLauncher
    {
        /// <summary>Handle dell'ultimo processo avviato DA QUESTO plugin (cmd.exe del .bat); null se mai avviato.</summary>
        public Process? LastStartedProcess { get; private set; }

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
                // §58 — agente in BACKGROUND: nessuna finestra, MAI. Percorso primario:
                // lancio DIRETTO di PHD2_Agent.exe (windowed, console=False) se esiste
                // accanto al .bat configurato → l'handle e' il PROCESSO AGENTE VERO
                // (proprieta' §58 perfetta: il fallback kill non dipende da un wrapper).
                // Fallback: setup custom (sorgente/venv) → cmd /c del .bat, nascosto.
                var dir = Path.GetDirectoryName(batPath) ?? "";
                var exePath = Path.Combine(dir, "PHD2_Agent.exe");
                ProcessStartInfo psi;
                if (File.Exists(exePath))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--config config.toml",
                        WorkingDirectory = dir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{batPath}\"",
                        WorkingDirectory = dir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                }
                LastStartedProcess = Process.Start(psi);
                Logger.Info($"AgentLauncher: started '{psi.FileName}' (hidden, §58 background)");
                return Task.FromResult(LaunchResult.Launched);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch Adaptive Agent: {ex.Message}");
                return Task.FromResult(LaunchResult.Error(ex.Message));
            }
        }
    }
}
