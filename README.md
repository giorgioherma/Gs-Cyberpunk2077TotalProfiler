# G's Cyberpunk 2077 TOTAL Profiler

Unified Windows controller for the three-part Cyberpunk 2077 profiling workflow:

- **GRSP 0.5.0** — REDscript profiler
- **CET Runtime Profiler 3.0.0-alpha6b** — CET/Lua profiler
- **CapFrameX** — external frametime capture; linked by the user and not version-locked
- **Native correlator** — combines GRSP + CET + CapFrameX into synchronized CSV/HTML/JSON/AI-readable output

## v0.2.14 — native Windows controller

The TOTAL Profiler controller and correlator are now **C# / .NET 8**. The Windows artifact is published as a normal **self-contained win-x64 folder**.

Users do **not** need:

- Python
- PyInstaller
- a separately installed .NET runtime

The app is not a self-extracting executable. The profiler payloads remain visible under `components/` rather than being hidden inside the controller EXE.

## Setup

v0.2.14 uses a three-step guided interface:

1. **SETUP** — select the Cyberpunk 2077 directory. Bundled CapFrameX 1.9.0 and local Results are used automatically. Custom CapFrameX/results paths are collapsed under Advanced setup.
2. **INSTALL & VERIFY** — choose installation options, install/verify GRSP + CET + CapFrameX configuration, and continue only when the visible checks pass.
3. **CAPTURE & RESULTS** — name the capture, launch CapFrameX/Cyberpunk, perform the shared F11 capture, collect results, compare, and open the report.

Current capture control:

- **F11 once** — starts GRSP + CET + CapFrameX.
- **F11 again** — stops all three and CET automatically exports its CSVs.

Before every capture, confirm CapFrameX's **Running processes** contains Cyberpunk 2077 only.

The app creates a canonical capture directory containing `Raw/`, `Combined/`, `CaptureManifest.json`, plus the final `*_FULL.zip` inside the same capture folder.

## Correlator interpretation

GRSP and CET are synchronized evidence layers. They are **not additive CPU accounting** and should not be blindly subtracted from CapFrameX frametime. Evidence labels such as `REDSCRIPT_HEAVY`, `CET_HEAVY`, `MIXED_SCRIPT_SIGNAL`, and `LARGELY_UNEXPLAINED` are diagnostic signals, not causal verdicts.

## Windows reputation / antivirus

The project is currently unsigned. A new unsigned executable can still receive SmartScreen/browser reputation warnings. v0.2.0 removes PyInstaller and the previous one-file/self-extracting packaging pattern. For a public production release, Authenticode code signing remains the correct long-term trust mechanism.


## CapFrameX bundling and compatibility

The Windows build bundles the official **CapFrameX 1.9.0 portable release** by default. This is the version used during TOTAL Profiler validation. Its upstream SHA-256 is verified during every GitHub Actions build, and the upstream CapFrameX license is shipped with the artifact.

TOTAL Profiler does **not** version-lock CapFrameX. The bundled 1.8.6 copy is selected by default, but users may select an existing `CapFrameX.exe` from another compatible version. CapFrameX 1.9.0 requires the .NET 10 Desktop Runtime. TOTAL Profiler only depends on the capture data it needs for correlation; unsupported future capture-schema changes should be reported as a compatibility error rather than rejected by version number.

CapFrameX remains a third-party project and is credited to its upstream authors.


## Bundled CapFrameX results default

TOTAL Profiler does not modify the bundled CapFrameX configuration or process ignore list. For the bundled CapFrameX copy, TOTAL Profiler defaults its **CapFrameX results** field to:

`Documents\CapFrameX\Captures`

The directory is created if needed and is valid while empty. A capture file is only required when **COLLECT RESULTS** is run.

## Package folder name

The GitHub Actions Windows artifact uses the short package name:

`GCTP-GCyberpunkTotalProfiler`

This keeps the extracted application path short. The executable name and in-app product name remain unchanged.


## v0.2.8 path reset and CapFrameX process guidance

Bundled CapFrameX results default to `Tools\CapFrameX\Portable\Captures`. An empty folder is valid before the first capture.

The Setup screen provides **Reset** for the CapFrameX results path and **Reset TOTAL results** for the TOTAL Profiler output path.

While profiling, CapFrameX's **Running processes** list should contain Cyberpunk 2077 only. If another process appears there, add it to CapFrameX's ignore list before recording the test.


## v0.2.9 UI / restore / CapFrameX updates

- Bundled default moved to the latest full stable **CapFrameX 1.9.0** portable release.
- TOTAL Profiler launches CapFrameX with its own folder as the working directory so CapFrameX relative resources, including capture start/stop sounds, resolve correctly.
- The CapFrameX process-list warning now appears beside the capture workflow/CET binding instructions.
- **RESET RESULT PATHS** resets both result locations without deleting captured data.
- **RESTORE ORIGINAL STATE** replaces separate CET/GRSP restore controls and restores all TOTAL Profiler-managed profiler state in one operation. Capture/result folders are preserved.


## v0.2.10 reset behavior

**RESET RESULT PATHS** only restores folder configuration and never deletes capture data.

**RESET CAPTURE STATE** is for incomplete/failed captures. It moves the current/latest raw GRSP capture, CET live profiler CSVs, and latest CapFrameX capture into `Results\Discarded\Reset_...`. This cleans the three-source capture state without uninstalling the profilers or deleting already-collected TOTAL Profiler results.

A reset timestamp prevents old source captures from being accidentally reused by the next **COLLECT RESULTS**. The same boundary is advanced after each successful collection.


## v0.2.11 CapFrameX capture defaults

CapFrameX upstream defaults capture time to 20 seconds. TOTAL Profiler overrides this for its profiling workflow:

- capture hotkey: F11
- capture time: 0 seconds (unlimited; second F11 stops)
- capture delay: 0
- hotkey sound mode: Voice
- start/stop sound level: 25%

The bundled portable AppSettings are seeded at build time, and **INSTALL PROFILERS** also reapplies these settings with a backup of an existing AppSettings file. Users therefore do not need to manually change the CapFrameX 20-second capture default.


## v0.2.12 CapFrameX portable-mode fix

The bundled CapFrameX now receives a real `portable.json` beside `CapFrameX.exe`. This is the switch CapFrameX 1.9.0 actually uses to enter portable mode; merely creating `Portable\Config` and `Portable\Captures` is not enough.

TOTAL Profiler now seeds/updates only `CaptureHotKey=F11`, `CaptureTime=0`, and `CaptureDelay=0`. CapFrameX keeps ownership of all other UI, sound, sensor, and capture settings. This avoids rewriting unrelated AppSettings fields and keeps the Capture page on upstream defaults.


## v0.2.13 capture workflow integration

- Window content is hosted in a real two-axis scroll panel; resizing to a shorter window now provides vertical scrolling as well as horizontal scrolling.
- The TOTAL Profiler CET control variant uses one input: first press starts; second press stops and immediately dumps CSV results.
- TOTAL Profiler writes the CETProfilerControls binding automatically to F11. Users no longer bind CET manually and there is no F12 export step in TOTAL Profiler.
- The standalone CET Runtime Profiler package is unchanged; this one-key behavior exists only in TOTAL Profiler's bundled control payload.
- COLLECT RESULTS verifies the app-side copies, then leaves CET live CSVs cleared and clears GRSP's game-side RESULTS directory.
- RESET RESULT PATHS was removed from the action bar; paths remain editable through Browse.
- The final `*_FULL.zip` is stored inside its corresponding `Capture_...\` directory, leaving one top-level item per capture.


## v0.2.14 guided workflow UI

- Replaces the single dense dashboard with three focused screens: **Setup → Install & Verify → Capture & Results**.
- Normal Setup shows only the Cyberpunk 2077 directory; CapFrameX executable/capture path and TOTAL Profiler result path are collapsed under **Custom paths / advanced setup**.
- Installation options now live on the install screen, including **CET core profiler only — leave existing 0-Engine untouched** and the GRSP scenario tag.
- The install screen exposes explicit verification for GRSP, CET, 0-Engine, CapFrameX, CET F11 and CapFrameX F11/unlimited configuration.
- The capture screen adds a user-defined capture name. This name is used in the `Capture_YYYYMMDD-HHMMSS_<name>` folder and manifest.
- Capture instructions are reduced to the normal workflow: launch CapFrameX + Cyberpunk, verify Cyberpunk is the only CapFrameX capture process, F11 start, play, F11 stop, close game, collect, compare.
- **RESET CAPTURE STATE** stays on the capture screen as a recovery action. **RESTORE ORIGINAL STATE** is collapsed under Advanced / recovery.
- The default window is smaller and no longer needs to be maximized for normal use.
