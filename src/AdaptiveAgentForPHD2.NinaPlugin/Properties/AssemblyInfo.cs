using System.Reflection;
using System.Runtime.InteropServices;

// GUID univoco generato per questo plugin.
// NON modificare dopo la prima distribuzione: NINA lo usa come chiave stabile.
[assembly: Guid("6F2E9C19-4F66-4F69-B7D3-E21D5AD7458B")]

[assembly: AssemblyTitle("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyDescription("Pannello dockable per NINA che mostra la dashboard web dell'Adaptive Agent for PHD2 su localhost:8080.")]
[assembly: AssemblyCompany("Alessandro Curci")]
[assembly: AssemblyProduct("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Alessandro Curci")]
[assembly: AssemblyVersion("1.2.3.0")]
[assembly: AssemblyFileVersion("1.2.3.0")]
[assembly: ComVisible(false)]

// --- Manifest plugin NINA ---
// Chiavi verificate dal template ufficiale isbeorn/nina.plugin.template
[assembly: AssemblyMetadata("Id",                    "6F2E9C19-4F66-4F69-B7D3-E21D5AD7458B")]
[assembly: AssemblyMetadata("Name",                  "Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyMetadata("Author",                "Alessandro Curci")]
[assembly: AssemblyMetadata("Homepage",              "https://t.me/+eewRNpvElSs5OWY8")]
[assembly: AssemblyMetadata("Repository",            "")]
[assembly: AssemblyMetadata("License",               "All rights reserved")]
[assembly: AssemblyMetadata("LicenseURL",            "")]
[assembly: AssemblyMetadata("Tags",                  "PHD2,Guiding,Dashboard,Adaptive Agent")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.0")]
[assembly: AssemblyMetadata("FeaturedImageURL",      "")]
[assembly: AssemblyMetadata("ChangelogURL",          "")]
[assembly: AssemblyMetadata("LongDescription",
    "Plugin minimale che aggiunge a NINA un pannello dockable contenente la " +
    "dashboard web dell'Adaptive Agent for PHD2 (http://localhost:8080). " +
    "Mostra la dashboard tramite WebView2 e, da v1.1, aggiunge sopra il pannello un " +
    "badge di stato dell'Agente (online/offline) e un pulsante 'Avvia Adaptive Agent' " +
    "che lancia il file Avvia.bat configurato nelle impostazioni del plugin. " +
    "Non interagisce con PHD2 ne' con il Sequencer di NINA: e' una pura rifinitura UX. " +
    "L'Agente puo' comunque essere avviato anche manualmente tramite Avvia.bat.")]
