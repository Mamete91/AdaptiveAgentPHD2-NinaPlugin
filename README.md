# Adaptive Agent for PHD2 — Dashboard (N.I.N.A. plugin)

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that integrates the **Adaptive Agent for PHD2** into an imaging session. Its centerpiece is a virtual **Sky Conditions monitor** — exposed through N.I.N.A.'s native `ISafetyMonitor` interface, the same role an ASCOM safety monitor plays — providing one continuously evaluated **SAFE/UNSAFE verdict on acquisition quality**. Since 1.14 the **guide channel judges the sky** in both directions; the imaging camera's transparency index is informational. **N.I.N.A.'s Sequence Engine always owns the sequence lifecycle**: the monitor reports, it never orchestrates. Around that state the plugin ships the recommended **Recovery probe** workflow to resume the session after clouds, a dockable dashboard panel, per-exposure telemetry forwarding, and automatic Agent lifecycle management. UI in English or Italiano.

The Adaptive Agent itself is a separate, standalone application. This plugin is the bridge between it and N.I.N.A.

> **Engine repository:** [Mamete91/AdaptiveAgentPHD2](https://github.com/Mamete91/AdaptiveAgentPHD2) — the adaptive guiding engine, its architecture documentation and the web dashboard.

---

## What is the Adaptive Agent?

PHD2 guides with fixed, user-set parameters, optimized frame by frame around the single correction pulse. Sky conditions are not fixed: seeing, transparency and target altitude drift over a night, and parameters tuned at dusk may be wrong by midnight.

The **Adaptive Agent** closes this outer loop. It observes guiding over minutes, adjusts **only two PHD2 guide-algorithm parameters — Aggressiveness and MinMove —** and, through its **Outcome-First controller**, keeps an adjustment only if the measured outcome (guiding RMS against a continuously measured baseline) actually improves. It never touches mount, calibration or dithering settings, and it restores the user's original parameters when it shuts down.

It is adaptive control, not machine learning: every decision is inspectable in the logs and on the **dashboard**, a local web UI served at `http://localhost:8080`.

All communication is HTTP on `localhost` only, using public APIs (PHD2's server protocol, N.I.N.A.'s plugin SDK). No external services are contacted.

---

## What the plugin does

Five functions. All optional, all graceful: **if the Agent is offline, N.I.N.A. is never disturbed** — no exceptions, no popups, the sequence continues.

### 1. Dockable dashboard panel
Renders the Agent dashboard inside N.I.N.A. through WebView2, with an online/offline status badge and a **Launch Adaptive Agent** button (starts the Agent's launcher script configured in the plugin settings). No separate browser window needed.

### 2. Per-exposure N.I.N.A. telemetry (N.I.N.A. → Agent)
On every saved light frame the plugin forwards N.I.N.A.'s image metrics to the Agent (`POST /nina/telemetry`): HFR, HFR standard deviation, star count, image statistics (mean/median/stdev ADU), exposure duration and filter. Fire-and-forget with a 3-second timeout and no retries. The Agent shows these metrics in its dashboard (sky transparency CLEAR / HAZE / CLOUD, star count, HFR): information only, they never decide safe or unsafe.

### 3. Sky Conditions monitor (Agent → N.I.N.A.)
A virtual device that N.I.N.A. can use like any other safety device — it appears under the *Safety Monitor* equipment category, which is N.I.N.A.'s name for the slot. It measures observing conditions continuously; reporting **unsafe** is one of the consequences, not the whole role. **The guide channel is the judge of the sky**, in both directions: the guide star sits in the same telescope as the imaging camera (off-axis guider) and is measured every few seconds, also while the sequence is paused. It needs **Adaptive Agent 3.1** or later; with an older Agent clouds are not judged and a warning says so (lost star, silent guide channel and lost Agent still are). It reports **unsafe** when:

- the guide star has been lost (**STAR_LOST**) beyond a consolidation time (default 5 minutes), measured on PHD2's own StarLost/GuideStep events;
- the **guide-star signal collapses** below half of its clear-sky reference (90 s to confirm in the Agent, then *Clouds → unsafe* polls: about 3.5 minutes with the factory settings) — clouds, or a guide-camera fault;
- the **guide channel goes silent** while guiding was expected;
- the **Agent becomes unreachable during an active session** — the monitor stays connected and escalates, it never flips to "safe" by disconnecting.

It returns **safe** when the guide star stays above 80% of its reference for a minute and is tracked steadily (about 2 minutes after the sky clears; five minutes of clear sky if the guide star or the guide exposure changed during unsafe, Adaptive Agent 3.2) — no verification exposure needed. The return after a lost star, a lost Agent or a silent guide channel goes through the same confirmation. During calibration, the Guiding Assistant and autofocus (when N.I.N.A. stops guiding for it) the sky verdict is suspended; a lost star still counts. The imaging camera keeps measuring the sky on every frame for the dashboard, for information only. Since 1.15 the imaging-camera judgement of 1.13 is gone from the code (to go back to it, reinstall 1.14.3).

**Never fails toward safe** (field-validated design, v1.5): losing reliable observation of the sky is itself treated as a risk condition. The return to safe always requires positive evidence (clear sky / stable guiding). The plugin only *reports* the state with its cause; N.I.N.A. decides what to do (pause, park) according to your safety policy. Every numeric threshold has a localized tooltip explaining its exact semantics.

**This device is the plugin's core.** Any safety-aware N.I.N.A. construct can consume its state — the global safety policy, *Wait until safe*, the *Loop while safe / Loop while unsafe* conditions, or your own *Trigger On Unsafe* workflow. The Recovery probe below is the **recommended** consumer for unattended recovery, not the only one.

### 4. Recovery probe — the session resumes on its own (sequencer instruction)
One workflow built **on top of** the monitor's state — the recommended one for unattended recovery. The **"Recovery probe (Adaptive Agent)"** instruction turns a clouded-out night into a self-recovering one; **"Recenter if needed (Adaptive Agent)"** makes sure the framing is right when it resumes. Recommended setup (two instructions, no extra containers):

```
Trigger On Unsafe
 └ Before Waiting For Safety
    └ Recovery probe (Adaptive Agent)
 └ After Waiting For Safety
    └ Recenter if needed (Adaptive Agent)
```

**It takes no exposures: it keeps the judge alive.** While conditions are unsafe it restarts guiding if guiding has stopped, if the guide star has been lost for longer than the minimum interval (after a long cloud the star may have drifted out of PHD2's search region), or once per *restart guiding after* interval of continued unsafe — postponed while the guide is confirming a clear sky, since a restart would reset the confirmation (Adaptive Agent 3.2); it never acts more often than the minimum interval and never on a parked mount. It ends on its own the moment the monitor returns SAFE, letting the sequence resume unattended. **Do not put instructions that stop guiding or park in the unsafe branch** (Park, Find Home, Stop Guiding): they would blind the only judge.

**Recenter if needed** (1.16) runs when the sky is clear again, in N.I.N.A.'s *After Waiting For Safety* container. Under clouds a meridian flip still completes, but N.I.N.A.'s own recenter is best effort: the plate solve fails, a notification is shown and the flip carries on — and the flip still reports success, so a plugin cannot tell (verified in the N.I.N.A. 3.3.0.1070 sources). The instruction does not guess: it **measures** with a single plate solve (two attempts at most), and only when there is a reason to doubt the framing — a meridian flip ran while the monitor was internally unsafe, or guiding was lost or stopped for at least *n* minutes of unsafe in total (12 by default; the mount tracked without corrections and a new guide lock keeps the drift). It recenters — guiding stopped, N.I.N.A.'s centering with your plate-solving settings, guiding restarted — only if the target is outside your N.I.N.A. centering tolerance; otherwise it touches nothing. Every check logs the unguided minutes and the measured offset, to tune *n* from real nights. Coordinates come from the target container (as with an inherited *Slew and center*). If anything goes wrong (mount parked or not tracking, camera missing, solve failing) it writes to the log and the sequence continues.

Put the Trigger On Unsafe in the **target container**, not at the sequence root: that way the end area (park, cover, power off) still runs when the night ends under clouds. Sequences saved with older versions load unchanged (the removed probe fields are simply ignored).

**Design guarantee — the Sequence Engine stays in charge.** The recovery loop runs entirely under N.I.N.A.'s own cancellation scope (triggers execute under the running container's linked cancellation chain — verified in the N.I.N.A. sources): when the sequence ends for any reason — end time reached, sun/moon/altitude limits (condition watchdogs interrupt within seconds), manual stop, N.I.N.A. closing — the probe loop is cancelled immediately. The plugin has no sequencer-control API at all, so once a sequence is over, a later return to SAFE changes a device flag and nothing else. The monitor protects an *active* sequence; it never becomes a second orchestrator of the session.

### 5. Agent lifecycle (on by default since v1.7)
The plugin **auto-launches the Agent** when N.I.N.A. starts (once the launcher path is configured) and **shuts it down gracefully** when N.I.N.A. closes — PHD2 baseline restored via the Agent's `POST /shutdown`, whose 200 response is a real contract: the Agent self-terminates via an internal watchdog even if its main loop is stuck, so N.I.N.A. closes instantly. Both behaviors can be disabled in the plugin options; by default the plugin only manages the Agent it launched or any reachable one (configurable).

---

## Architecture

```
N.I.N.A. ──ImageSaved metrics──▶  Adaptive Agent  ◀──guide events── PHD2
 plugin  ◀──/status (safety)───  localhost:8080   ──Aggr/MinMove──▶
    │
    └── dockable WebView2 panel ──▶ dashboard
```

The plugin reads the Agent's HTTP endpoints (`/about` for health, `/status` for the safety state) as plain JSON, which keeps it version-agnostic with respect to the Agent.

---

## Requirements

| Requirement | Notes |
|-------------|-------|
| **N.I.N.A. 3.2 or later** | Compatible with N.I.N.A. **3.2 and later, including 3.3**. Built against the N.I.N.A. 3.2.0.9001 SDK |
| **Adaptive Agent for PHD2** | The [engine package](https://github.com/Mamete91/AdaptiveAgentPHD2) must be running locally |
| **WebView2 Runtime** | Preinstalled on Windows 11 and updated Windows 10. If the panel appears blank, install it from [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

---

## Installation

Building requires the **.NET 8 SDK** (x64):

```powershell
cd src\AdaptiveAgentForPHD2.NinaPlugin
dotnet build -c Release
```

Then, from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-plugin.ps1
```

The plugin DLL is copied to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`. Restart N.I.N.A. to load it.

*(Distribution through the N.I.N.A. Plugin Manager is planned.)*

---

## Configuration

In N.I.N.A.: **Options → Plugins → Adaptive Agent for PHD2 — Dashboard**.

| Setting | Default | Notes |
|---------|---------|-------|
| Plugin language | *Follow N.I.N.A.* | English / Italiano, switches the plugin UI live |
| Path to `Avvia.bat` (Agent launcher) | *(empty)* | Used by auto-launch and the **Launch Adaptive Agent** button |
| Auto-launch the Agent | **on** | Starts the Agent when N.I.N.A. starts (inert until the path above is set) |
| Manage an external Agent | **on** | On N.I.N.A. close, gracefully shut down any reachable Agent (turn off if you run it standalone) |
| Health-check interval (s) | `15` | How often the plugin polls the Agent (range 5–120) |
| Dashboard URL | `http://127.0.0.1:8080` | Change only if you changed the Agent's port |
| Forward per-exposure telemetry | **on** | Toggle for function 2 above (information only) |
| Cloud safety enabled | **on** | The guide-star collapse is the cloud path. Off = no cloud ever leads to unsafe |
| Clouds → unsafe (polls) | `8` | How long the guide-star signal must stay collapsed after the Agent's 90 s confirmation |
| Clear → safe (polls) | `4` | Polls of guide-confirmed clear sky to return safe (also after a lost star, lost Agent, silent channel) |
| Lost star — consolidation (s) | `300` | How long the guide star must stay lost before unsafe |
| Silent guide channel → unsafe | **on** | Silence (s) and polls before unsafe while guiding is expected |
| Agent unreachable → unsafe | **on** | Escalates only during an active session |
| Meridian protection | **on** | Lead time (min) before the flip deadline; the window covers N.I.N.A.'s whole flip |

Settings are stored in `%LOCALAPPDATA%\NINA\Plugins\AdaptiveAgentForPHD2.NinaPlugin\settings.json`.

---

## Usage

1. Open N.I.N.A. and dock the **Adaptive Agent for PHD2** panel from the **Window** menu.
2. Set the path to the Agent's `Avvia.bat` in the plugin settings (first time only).
3. From then on the Agent starts with N.I.N.A. and stops (baseline restored) when N.I.N.A. closes. The **Launch Adaptive Agent** button remains as a manual fallback.
4. Within a few seconds the badge turns to *online* and the dashboard loads.
5. Extract the Agent package anywhere, point *Agent launcher path (Avvia.bat)* at its `Avvia.bat` (re-select it after every Agent upgrade — the saved path still points at the old folder), then connect the **Adaptive Agent for PHD2 — Sky Conditions** device under *Equipment → Safety Monitor* and add the **Recovery probe** instruction inside a *Trigger On Unsafe* (see function 4) for unattended cloud recovery.

---

## Changelog (summary)

| Version | Highlights |
|---------|-----------|
| **1.16** | **Recenter if needed (Adaptive Agent)**, a new instruction for *After Waiting For Safety*: when the sky is clear again it checks the framing with one plate solve after a meridian flip that ran while unsafe (N.I.N.A.'s recenter can fail silently under clouds) or after the set minutes without guiding (12 by default), and recenters only if the target is outside the N.I.N.A. centering tolerance. Both instructions carry an ⓘ with a bilingual explanation (follows the plugin language), and their descriptions in the instruction list are in English and Italian. |
| 1.15.1 | Texts only, no logic change: settings help, tooltips and toasts checked against the code. Factory values and allowed ranges in the tooltips; what a "poll" is (one check every *n* seconds, so the poll counts follow the check interval); "with cloud safety on" where the return to safe waits for the guide's clear sky; the Trigger On Unsafe goes in the target container; the meridian toasts no longer claim an unsafe hold after the flip, and the timeout toast names both cases (no flip within 20 min, or a flip still running after 30). Pairs with Adaptive Agent 3.3.1 (dashboard texts). |
| **1.15** | **Leaner plugin: the 1.13 imaging-camera judgement is removed** (index-based cloud persistence, stale telemetry, judge switching, verification exposures, last-light memory). Options page reorganised into sections; the nine settings of the camera judgement are gone (with Agent 3.1+ and its sky sensor on they did nothing; they only acted as a fallback with an older Agent or the sensor off, where clouds are now not judged and a warning says so), and the cloud threshold is always editable (it used to be greyed out by a camera-only checkbox). A saved *The imaging camera can also report unsafe* is ignored; going back to 1.14.3 restores the camera settings to their defaults. Recovery probe: only *Restart guiding after* and *Minimum interval* — the restart field is editable again (it was greyed out while *Auto cadence* was on, i.e. by default). Older settings and sequences load unchanged. With an Agent older than 3.1 clouds are not judged and a warning says so |
| 1.14.3 | Meridian protection covers N.I.N.A.'s whole flip: the window stays open from N.I.N.A.'s flip start to its end (recenter, new guide star, guiding resumed), instead of closing when the pier side changes — on 20-21/11 the honest unsafe came back mid-flip and Trigger On Unsafe cancelled the recenter. Tracking is never touched while N.I.N.A. is flipping, and an unsafe arriving during a flip no longer interrupts it |
| 1.14.2 | Option texts aligned with the guide-channel judge: the return to safe after a lost star, a lost Agent or a silent guide channel also waits for the guide to confirm a clear sky (five minutes after a guide-star or exposure change during unsafe, Adaptive Agent 3.2) |
| 1.14.1 | The guide probe no longer restarts guiding while the guide is confirming a clear sky: after a guide-star or exposure change during unsafe, Adaptive Agent 3.2 asks for five minutes of clear sky, and a restart would reset the count |
| **1.14** | **The guide channel judges the sky**, in both directions (needs Adaptive Agent 3.1): unsafe on a collapsed guide-star signal, safe again when the guide star confirms a clear sky — no verification exposure. The imaging camera becomes informational (the 1.13 judgement stays available as an option). STAR_LOST read on PHD2 events, so one dropped frame no longer sticks. Calibration, Guiding Assistant and autofocus never count as evidence. The Recovery probe becomes a *guide probe*: it restarts guiding when needed instead of taking exposures. A monitor reconnected after an unsafe no longer stays unsafe forever |
| 1.13 | Focus state travels with each exposure |
| **1.12** | Renamed to **Sky Conditions** — the device measures observing conditions; reporting unsafe is one consequence, not the whole role (N.I.N.A. stores the device by Id, so existing profiles keep working) · **separate fast and slow paths**: the guide channel (~3 s, reacts to *how fast* the sky degrades) and the imaging camera (~1 exposure, confirms *how long* it stays degraded) now have independent accumulators and thresholds, so tuning one no longer moves the other |
| 1.11 | Meridian protection window · sky-degradation evidence from the guide channel · probe channel-ready gate |
| **1.7** | Agent lifecycle (auto-launch + graceful shutdown with baseline restore, on by default) · self-contained **Recovery probe** loop · instant N.I.N.A. close (Agent self-kill watchdog) · UI localized EN/IT with live switch · parameter tooltips |
| 1.6 | Cloud-recovery sequencer instruction (S1 timeout fail-safe + S2 guide-SNR hint) |
| 1.5 | Safety Monitor hardened after field validation: index-based cloud persistence (leaky accumulator), stale telemetry → unsafe, Agent loss → unsafe — never fails toward safe |
| 1.4 | Safety Monitor: cloud condition on N.I.N.A. transparency alongside STAR_LOST |
| 1.3 | Per-exposure telemetry forwarding to the Agent (fire-and-forget) |
| 1.1 | Agent status badge and Launch button above the panel |
| 1.0 | Dockable WebView2 panel embedding the dashboard |

---

## Community & support

Official community and beta-testing group (Telegram): https://t.me/+eewRNpvElSs5OWY8

## License

**BSD-3-Clause** — see [`LICENSE`](LICENSE).

Copyright (c) 2026 Alessandro Curci.
