# Adaptive Agent for PHD2 — Dashboard (N.I.N.A. plugin)

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that integrates the **Adaptive Agent for PHD2** into an imaging session: a dockable dashboard panel, per-exposure telemetry forwarding, and a Safety Monitor that reacts to persistent clouds and lost guide stars.

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

Three functions. All optional, all fail-safe: **if the Agent is offline, N.I.N.A. is never disturbed** — no exceptions, no popups, the sequence continues.

### 1. Dockable dashboard panel
Renders the Agent dashboard inside N.I.N.A. through WebView2, with an online/offline status badge and a **Launch Adaptive Agent** button (starts the Agent's launcher script configured in the plugin settings). No separate browser window needed.

### 2. Per-exposure N.I.N.A. telemetry (N.I.N.A. → Agent)
On every saved light frame the plugin forwards N.I.N.A.'s image metrics to the Agent (`POST /nina/telemetry`): HFR, HFR standard deviation, star count, image statistics (mean/median/stdev ADU), exposure duration and filter. Fire-and-forget with a 3-second timeout and no retries. The Agent uses these metrics to recognize **sky transparency** (CLEAR / HAZE / CLOUD) independently of guiding.

### 3. Safety Monitor (Agent → N.I.N.A.)
A virtual **Safety Monitor** device that N.I.N.A. can use like any other safety device. It reports **unsafe** when:

- the guide star has been lost (**STAR_LOST**) beyond a consolidation time (default 5 minutes), or
- N.I.N.A. transparency has stayed **CLOUD** for several consecutive polls (asymmetric hysteresis: slow toward unsafe, faster back to safe — a brief haze does not trigger it).

**Fail-safe by design:** if the Agent is offline or its telemetry is stale, the cloud condition stays neutral — no spurious unsafe. The plugin only *reports* the state with its cause; N.I.N.A. decides what to do (pause, park) according to your safety policy.

> ⚠️ **Important:** for continuous protection, place a **"Wait Until Safe"** instruction **inside your acquisition loop** (not only at the start of the sequence). Otherwise the Safety Monitor is consulted once and clouds arriving mid-sequence will not pause the imaging run.

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
| Path to `Avvia.bat` (Agent launcher) | *(empty)* | Used by the **Launch Adaptive Agent** button |
| Health-check interval (s) | `15` | How often the plugin polls the Agent (range 5–120) |
| Dashboard URL | `http://localhost:8080` | Change only if you changed the Agent's port |
| Forward per-exposure telemetry | **on** | Toggle for function 2 above |
| Cloud safety enabled | **on** | Toggle for the CLOUD condition, plus its unsafe/safe poll thresholds |
| STAR_LOST consolidation (s) | `300` | How long STAR_LOST must persist before unsafe |

Settings are stored in `%LOCALAPPDATA%\NINA\Plugins\AdaptiveAgentForPHD2.NinaPlugin\settings.json`.

---

## Usage

1. Open N.I.N.A. and dock the **Adaptive Agent for PHD2** panel from the **Window** menu.
2. Set the path to the Agent's `Avvia.bat` in the plugin settings (first time only).
3. Start the Agent with the **Launch Adaptive Agent** button (or run `Avvia.bat` manually).
4. Within a few seconds the badge turns to *online* and the dashboard loads.
5. Optionally connect the **Adaptive Agent Safety Monitor** as a safety device and use *Wait Until Safe* in your sequence.

---

## Changelog (summary)

| Version | Highlights |
|---------|-----------|
| **1.4** | Safety Monitor: cloud condition on N.I.N.A. transparency (asymmetric hysteresis, fail-safe) alongside STAR_LOST |
| 1.3 | Per-exposure telemetry forwarding to the Agent (fire-and-forget) |
| 1.1 | Agent status badge and Launch button above the panel |
| 1.0 | Dockable WebView2 panel embedding the dashboard |

---

## Community & support

Official community and beta-testing group (Telegram): https://t.me/+eewRNpvElSs5OWY8

## License

**BSD-3-Clause** — see [`LICENSE`](LICENSE).

Copyright (c) 2026 Alessandro Curci.
