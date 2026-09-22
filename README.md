# G's Cyberpunk 2077 TOTAL Profiler

Unified Windows controller for the three-part Cyberpunk 2077 profiling workflow:

- **GRSP 0.5.0** — REDscript profiler
- **CET Runtime Profiler 3.0.0-alpha6b** — CET/Lua profiler
- **CapFrameX** — external frametime capture; linked by the user and not version-locked
- **Native correlator** — combines GRSP + CET + CapFrameX into synchronized CSV/HTML/JSON/AI-readable output

## v0.2.5 — native Windows controller

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

The Windows build bundles the official **CapFrameX 1.8.6 portable release** by default. This is the version used during TOTAL Profiler validation. Its upstream SHA-256 is verified during every GitHub Actions build, and the upstream CapFrameX license is shipped with the artifact.

TOTAL Profiler does **not** version-lock CapFrameX. The bundled 1.8.6 copy is selected by default, but users may select an existing `CapFrameX.exe` from another compatible version. CapFrameX 1.8.6 requires the .NET 9 Desktop Runtime; CapFrameX 1.9+ requires the .NET 10 Desktop Runtime. TOTAL Profiler only depends on the capture data it needs for correlation; unsupported future capture-schema changes should be reported as a compatibility error rather than rejected by version number.

CapFrameX remains a third-party project and is credited to its upstream authors.


## Bundled CapFrameX portable defaults

For the bundled CapFrameX 1.8.6 copy, TOTAL Profiler supplies the standard portable layout explicitly:

- captures: `Tools\CapFrameX\Portable\Captures`
- config: `Tools\CapFrameX\Portable\Config`
- logs: `Tools\CapFrameX\Portable\Logs`

The capture directory is created in the artifact and is considered valid while empty; it does not need a JSON capture until results are actually collected.

The bundled portable process list starts with `dwm` (Windows Desktop Window Manager) ignored. This keeps the CapFrameX running-process list clear before Cyberpunk starts, while users remain free to edit the CapFrameX ignore list themselves.
