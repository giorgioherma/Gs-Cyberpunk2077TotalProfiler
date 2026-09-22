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

Choose:

- Cyberpunk 2077 directory
- `CapFrameX.exe`
- CapFrameX results folder (auto-detected when possible; manually selectable for custom layouts)
- TOTAL Profiler results directory — defaults to `Results\` beside the TOTAL Profiler executable

Current capture controls:

- **F11** — shared profiling start/stop for GRSP + CET + CapFrameX
- **F12** — CET result export only

CET still requires the two bindings to be set once in **CET > Bindings** after installation.

## Capture workflow

1. Launch CapFrameX and Cyberpunk 2077.
2. Press **F11** to start GRSP + CET + CapFrameX.
3. Play/test.
4. Press **F11** again to stop all three. GRSP finalizes on this second F11.
5. Press **F12** to export CET CSVs.
6. Close Cyberpunk 2077.
7. Click **COLLECT RESULTS**.
8. Click **COMPARE RESULTS**.

The app creates a canonical capture directory containing `Raw/`, `Combined/`, `CaptureManifest.json`, plus a full shareable ZIP.

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


## v0.2.14 — Pass 13 features with v0.2.12 CapFrameX frozen

v0.2.14 keeps the desired v0.2.13 TOTAL Profiler changes:

- one shared **F11** for CET start / stop + automatic CET CSV export
- the responsive two-axis scrolling UI
- verified post-collection cleanup of live GRSP/CET source results
- the final `*_FULL.zip` stored inside its matching `Capture_...\` folder

CapFrameX is deliberately frozen to the exact TOTAL Profiler **v0.2.12 integration**. No CapFrameX source patch or custom CapFrameX build is applied. The Windows workflow continues to download the official stable CapFrameX 1.9.0 portable release and verifies the exact upstream archive SHA-256:

`00d56035681b975cee2fa392b93024ad60aaf9c0d1fcea91e8cb9c0fb6f1803a`

The v0.2.12 CapFrameX configuration behavior is retained unchanged: bundled portable mode, F11 capture hotkey, CaptureTime=0, CaptureDelay=0, and CapFrameX owns all other UI/sound settings.
