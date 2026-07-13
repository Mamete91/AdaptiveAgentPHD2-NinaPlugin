using System.Reflection;
using System.Runtime.InteropServices;

// GUID univoco generato per questo plugin.
// NON modificare dopo la prima distribuzione: NINA lo usa come chiave stabile.
[assembly: Guid("6F2E9C19-4F66-4F69-B7D3-E21D5AD7458B")]

[assembly: AssemblyTitle("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyDescription("Dockable N.I.N.A. panel embedding the Adaptive Agent for PHD2 web dashboard (localhost:8080), with per-exposure telemetry forwarding and a guide/sky Safety Monitor.")]
[assembly: AssemblyCompany("Alessandro Curci")]
[assembly: AssemblyProduct("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Alessandro Curci")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: ComVisible(false)]

// --- Manifest plugin NINA ---
// Chiavi verificate dal template ufficiale isbeorn/nina.plugin.template
[assembly: AssemblyMetadata("Id",                    "6F2E9C19-4F66-4F69-B7D3-E21D5AD7458B")]
[assembly: AssemblyMetadata("Name",                  "Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyMetadata("Author",                "Alessandro Curci")]
[assembly: AssemblyMetadata("Homepage",              "https://t.me/+eewRNpvElSs5OWY8")]
[assembly: AssemblyMetadata("Repository",            "https://github.com/Mamete91/AdaptiveAgentPHD2-NinaPlugin")]
[assembly: AssemblyMetadata("License",               "BSD-3-Clause")]
[assembly: AssemblyMetadata("LicenseURL",            "https://raw.githubusercontent.com/Mamete91/AdaptiveAgentPHD2-NinaPlugin/master/LICENSE")]
[assembly: AssemblyMetadata("Tags",                  "PHD2,Guiding,Dashboard,Adaptive Agent")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("FeaturedImageURL",      "")]
[assembly: AssemblyMetadata("ChangelogURL",          "")]
[assembly: AssemblyMetadata("LongDescription",
    "Integrates the Adaptive Agent for PHD2 into N.I.N.A.: a dockable panel embedding the " +
    "Agent web dashboard (http://localhost:8080) via WebView2, with an online/offline status " +
    "badge and a 'Launch Adaptive Agent' button. Forwards N.I.N.A. per-exposure metrics (HFR, " +
    "star count, image statistics) to the Agent on every saved light frame — optional and " +
    "graceful: if the Agent is offline, N.I.N.A. is never disturbed. Provides a virtual Safety " +
    "Monitor that reports unsafe on sustained STAR_LOST, persistent sky-transparency degradation " +
    "(index-based, v1.5), stale telemetry under a degraded sky, or Agent loss during an active " +
    "session. Adds the 'Wait for recovery hint (Adaptive Agent)' sequencer instruction (v1.6): a " +
    "timing gate for cloud-recovery probe exposures — probes fire on timeout (fail-safe) or earlier " +
    "when guide-star SNR suggests the sky is recovering; the probe image (N1) remains the only path " +
    "back to safe. All communication is local (localhost); no external services.")]
