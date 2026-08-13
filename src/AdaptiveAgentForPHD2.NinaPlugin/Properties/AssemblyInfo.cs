using System.Reflection;
using System.Runtime.InteropServices;

// GUID univoco generato per questo plugin.
// NON modificare dopo la prima distribuzione: NINA lo usa come chiave stabile.
[assembly: Guid("6F2E9C19-4F66-4F69-B7D3-E21D5AD7458B")]

[assembly: AssemblyTitle("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyDescription("Dockable N.I.N.A. panel embedding the Adaptive Agent for PHD2 web dashboard (127.0.0.1:8080), with per-exposure telemetry forwarding and a sky-conditions monitor (N.I.N.A. safety device).")]
[assembly: AssemblyCompany("Alessandro Curci")]
[assembly: AssemblyProduct("Adaptive Agent for PHD2 — Dashboard")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Alessandro Curci")]
[assembly: AssemblyVersion("1.12.2.0")]
[assembly: AssemblyFileVersion("1.12.2.0")]
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
    "Integrates the Adaptive Agent for PHD2 into N.I.N.A. The Agent tunes PHD2 guiding in real " +
    "time; this plugin gives it eyes on the imaging session and a voice inside the sequencer." +
    "\n\n" +
    "DOCKABLE PANEL - embeds the Agent web dashboard (http://127.0.0.1:8080) via WebView2, with " +
    "an online/offline badge and a Launch Adaptive Agent button. The dashboard reads at a glance: " +
    "five fixed slots (adaptive control, guiding, sky, session, recovery) say what is happening, " +
    "contextual icons appear only when something needs attention, and the numbers with their " +
    "source live in the tooltips." +
    "\n\n" +
    "TELEMETRY - per-exposure metrics from N.I.N.A. (HFR, star count, image statistics) are " +
    "forwarded to the Agent on every saved light frame. Optional and graceful: if the Agent is " +
    "offline, N.I.N.A. is never disturbed." +
    "\n\n" +
    "SKY CONDITIONS MONITOR - a virtual device that measures observing conditions continuously " +
    "and reports them through the N.I.N.A. safety-device interface (it appears under the Safety " +
    "Monitor equipment category, which is the N.I.N.A. name for the slot, not this monitor's " +
    "role). It reports unsafe on any of six independent conditions: sustained STAR_LOST; " +
    "persistent sky-transparency degradation measured on the imaging camera star count; a " +
    "sustained collapse of the guide-star signal, which the guide channel sees minutes before the " +
    "next light frame could; stale telemetry under an already degraded sky; loss of the Agent " +
    "during an active session; and a guide channel gone silent while guiding was expected. Fast " +
    "evidence (guide channel, seconds) and persistent evidence (imaging camera, one exposure) " +
    "have separate accumulators and separate thresholds, so tuning one never slows the other. " +
    "Recovery toward safe is granted only by the imaging camera: one guide star can testify that " +
    "the sky went bad, never that the whole field came back. The monitor only ever reports - the " +
    "N.I.N.A. Sequence Engine always stays in charge of the sequence." +
    "\n\n" +
    "MERIDIAN PROTECTION - a bounded window (on by default) that lets the mechanical flip run at " +
    "its deadline even while conditions are unsafe, then restores the unsafe hold immediately. " +
    "Without it, N.I.N.A. stops tracking at the deadline and nothing ever restarts it: the guide " +
    "star drifts away and the night ends there, even if the sky clears. The window authorises the " +
    "manoeuvre only; it never passes judgement on the sky." +
    "\n\n" +
    "SELF-RECOVERY - the self-contained Recovery probe (Adaptive Agent) sequencer instruction " +
    "turns a clouded-out night into a self-recovering one. Placed alone inside Trigger On Unsafe, " +
    "it loops while conditions are unsafe and takes unguided verification exposures replicating " +
    "the interrupted light: on probe timeout, or earlier when the guide-star signal hints the sky " +
    "is recovering, and deferred while the guide channel is not yet stable enough for the result " +
    "to mean anything. The probe image remains the only path back to safe, and the loop ends on " +
    "its own once the monitor returns safe." +
    "\n\n" +
    "AGENT LIFECYCLE - on by default: auto-launches the Agent when N.I.N.A. starts and shuts it " +
    "down gracefully on close, restoring the PHD2 baseline." +
    "\n\n" +
    "Plugin UI in English or Italiano (follows N.I.N.A. by default, switchable live). All " +
    "communication is local; no external services.")]
