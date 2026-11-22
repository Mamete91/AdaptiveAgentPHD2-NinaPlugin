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
[assembly: AssemblyVersion("1.16.0.0")]
[assembly: AssemblyFileVersion("1.16.0.0")]
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
[assembly: AssemblyMetadata("FeaturedImageURL",      "https://raw.githubusercontent.com/Mamete91/AdaptiveAgentPHD2-NinaPlugin/master/docs/img/logo.png")]
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
    "role). The guide channel is the judge of the sky, both to pause the sequence and to let it " +
    "resume: the guide star sits in " +
    "the same telescope as the imaging camera (off-axis guider) and is sampled every few seconds, " +
    "also while the sequence is paused (requires Adaptive Agent 3.1 or later; with an older Agent " +
    "clouds are not judged and a warning says so). It reports unsafe on: sustained " +
    "STAR_LOST; a guide-star signal below half of its clear-sky reference (about 3.5 minutes with the " +
    "factory settings - clouds, or a guide-camera fault); a guide channel gone silent while guiding " +
    "was expected; loss of the Agent during an active session. It returns safe when the guide star " +
    "stays above 80% of its reference for a minute and is tracked steadily (about 2 minutes with the " +
    "factory settings); after a lost star, a lost Agent or a silent guide channel it waits for the " +
    "same clear-sky confirmation. During calibration, the Guiding Assistant and autofocus (when N.I.N.A. stops guiding for " +
    "it) the sky verdict is suspended; a lost star still counts. The imaging camera keeps measuring " +
    "the sky on every frame, for information only. The monitor only ever reports - the N.I.N.A. Sequence Engine always " +
    "stays in charge of the sequence." +
    "\n\n" +
    "MERIDIAN PROTECTION - a bounded window (on by default) that lets the mechanical flip run at " +
    "its deadline even while conditions are unsafe, and covers N.I.N.A.'s whole flip (recenter and " +
    "guiding resumed included) before restoring the unsafe hold. " +
    "Without it, N.I.N.A. stops tracking at the deadline and nothing ever restarts it: the guide " +
    "star drifts away and the night ends there, even if the sky clears. The window authorises the " +
    "manoeuvre only; it never passes judgement on the sky." +
    "\n\n" +
    "SELF-RECOVERY - the Recovery probe (Adaptive Agent) sequencer instruction takes no exposures: " +
    "placed in Trigger On Unsafe it keeps the judge alive - it restarts guiding if guiding has " +
    "stopped, if the guide star has been lost for longer than the minimum interval, or once per " +
    "restart interval of continued unsafe (postponed while the guide is confirming a clear sky) - " +
    "and ends on its own when the monitor returns safe. The unsafe branch must not stop guiding or park. " +
    "The Recenter if needed (Adaptive Agent) instruction, placed in After Waiting For Safety, checks the " +
    "framing with a single plate solve when the sky is clear again - only after a meridian flip ran while " +
    "unsafe (under clouds the flip's own recenter can fail silently) or after the set minutes without " +
    "guiding - and recenters only if the target is outside the N.I.N.A. centering tolerance." +
    "\n\n" +
    "AGENT LIFECYCLE - on by default: auto-launches the Agent when N.I.N.A. starts and shuts it " +
    "down gracefully on close, putting PHD2's starting parameters back." +
    "\n\n" +
    "Plugin UI in English or Italiano (follows N.I.N.A. by default, switchable live). All " +
    "communication is local; no external services.")]
