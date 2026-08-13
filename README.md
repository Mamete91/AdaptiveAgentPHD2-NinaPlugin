# Adaptive Agent for PHD2 — Dashboard (N.I.N.A. plugin)

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that integrates the **Adaptive Agent for PHD2** into an imaging session. Its centerpiece is a virtual **Sky Conditions monitor** — exposed through N.I.N.A.'s native `ISafetyMonitor` interface, the same role an ASCOM safety monitor plays — providing one continuously evaluated **SAFE/UNSAFE verdict on acquisition quality** (guide star, N1 sky transparency, telemetry freshness, Agent health). **N.I.N.A.'s Sequence Engine always owns the sequence lifecycle**: the monitor reports, it never orchestrates. Around that state the plugin ships the recommended **Recovery probe** workflow to resume the session after clouds, a dockable dashboard panel, per-exposure telemetry forwarding, and automatic Agent lifecycle management. UI in English or Italiano.

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
On every saved light frame the plugin forwards N.I.N.A.'s image metrics to the Agent (`POST /nina/telemetry`): HFR, HFR standard deviation, star count, image statistics (mean/median/stdev ADU), exposure duration and filter. Fire-and-forget with a 3-second timeout and no retries. The Agent uses these metrics to recognize **sky transparency** (CLEAR / HAZE / CLOUD) independently of guiding.

### 3. Sky Conditions monitor (Agent → N.I.N.A.)
A virtual device that N.I.N.A. can use like any other safety device — it appears under the *Safety Monitor* equipment category, which is N.I.N.A.'s name for the slot. It measures observing conditions continuously; reporting **unsafe** is one of the consequences, not the whole role. It reports **unsafe** when:

- the guide star has been lost (**STAR_LOST**) beyond a consolidation time (default 5 minutes);
- N.I.N.A. sky transparency has stayed **degraded persistently** — an index-based leaky accumulator: clear sky drains the count, brief HAZE bounces are neutral and never reset it (v1.5);
- N.I.N.A. telemetry has gone **stale while the last known sky was degraded** (v1.5);
- the **Agent becomes unreachable during an active session** (v1.5) — the monitor stays connected and escalates, it never flips to "safe" by disconnecting.

**Never fails toward safe** (field-validated design, v1.5): losing reliable observation of the sky is itself treated as a risk condition. The return to safe always requires positive evidence (clear sky / stable guiding). The plugin only *reports* the state with its cause; N.I.N.A. decides what to do (pause, park) according to your safety policy. Every numeric threshold has a localized tooltip explaining its exact semantics.

**This device is the plugin's core.** Any safety-aware N.I.N.A. construct can consume its state — the global safety policy, *Wait until safe*, the *Loop while safe / Loop while unsafe* conditions, or your own *Trigger On Unsafe* workflow. The Recovery probe below is the **recommended** consumer for unattended recovery, not the only one.

### 4. Recovery probe — the session resumes on its own (sequencer instruction)
One workflow built **on top of** the monitor's state — the recommended one for unattended recovery. The **"Recovery probe (Adaptive Agent)"** instruction turns a clouded-out night into a self-recovering one. Recommended setup (one instruction, no extra containers):

```
Trigger On Unsafe
 └ Before Waiting For Safety
    └ Recovery probe (Adaptive Agent)
```

While conditions are unsafe it takes ONE unguided verification exposure — replicating your last light frame (exposure/gain/offset/binning) — at a configurable cadence (probe timeout, fail-safe) or earlier when the Agent's guide-star SNR hints the sky is recovering, never more often than the minimum interval. The saved probe refreshes the Agent's transparency index — **the probe image is the only path back to safe** — and the loop ends on its own the moment the monitor returns SAFE, letting the sequence resume unattended.

**Design guarantee — the Sequence Engine stays in charge.** The recovery loop runs entirely under N.I.N.A.'s own cancellation scope (triggers execute under the running container's linked cancellation chain — verified in the N.I.N.A. sources): when the sequence ends for any reason — end time reached, sun/moon/altitude limits (condition watchdogs interrupt within seconds), manual stop, N.I.N.A. closing — the probe loop is cancelled immediately, even mid-exposure. The plugin has no sequencer-control API at all, so once a sequence is over, a later return to SAFE changes a device flag and nothing else. The monitor protects an *active* sequence; it never becomes a second orchestrator of the session.

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
| Dashboard URL | `http://localhost:8080` | Change only if you changed the Agent's port |
| Forward per-exposure telemetry | **on** | Toggle for function 2 above |
| Cloud safety enabled | **on** | Index-based persistence thresholds, each with a localized tooltip |
| Stale telemetry → unsafe | **on** | Escalates only during an active session with the last known sky degraded |
| Agent lost → unsafe | **on** | Escalates only during an active session |
| STAR_LOST consolidation (s) | `300` | How long STAR_LOST must persist before unsafe |

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
