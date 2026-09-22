# G's Cyberpunk 2077 TOTAL Profiler — v0.1.0 prototype

One Windows controller for the full profiling workflow:

- **GRSP 0.5.0 Public Preview** — bundled exact compiled GameRoot build.
- **CET Runtime Profiler 3.0.0-alpha6b** — bundled exact profiler payload and the existing safe 0-Engine/Core installation logic.
- **CapFrameX** — **external**. The user links `CapFrameX.exe`; TOTAL Profiler does not bundle or version-lock CapFrameX.
- **GRSP Correlator 0.1.2** — bundled as the internal comparison engine.

## What the app owns

The app remembers:

- Cyberpunk 2077 game directory;
- CapFrameX executable;
- CapFrameX capture/results directory;
- TOTAL Profiler result directory;
- GRSP scenario label;
- CET 0-Engine mode selection.

The chosen TOTAL Profiler result directory is the canonical location for collected and correlated captures. Nothing is intentionally written beside a random source ZIP.

## Keys

The current tested stack uses:

- **F11** — shared profiling toggle: GRSP + CET + CapFrameX.
- **F12** — CET `CREATE CSV` / result export only.

GRSP 0.5.0 has F11 compiled into the current DLL.

CET deliberately does not allow a mod to define its own default bindings. After installation, bind these once in **CET > Bindings**:

- `Profiler: START / PAUSE / RESUME` → **F11**
- `Profiler: CREATE CSV` → **F12**

TOTAL Profiler makes a best-effort attempt to set CapFrameX's existing `CaptureHotKey` setting to F11. If the linked CapFrameX version/configuration does not expose that setting in a recognized JSON file, it leaves the configuration alone and tells the user to set F11 manually.

## First setup

1. Select the Cyberpunk 2077 directory.
2. Select `CapFrameX.exe`.
3. Confirm or override the detected CapFrameX results directory.
4. Choose the TOTAL Profiler results directory.
5. Leave normal CET integration selected, or choose **Core profiler only — leave 0-Engine untouched**.
6. Click **INSTALL PROFILERS**.
7. On the first game run, set the two CET bindings above.

## Capture workflow

1. Launch CapFrameX and Cyberpunk 2077.
2. Press **F11** to start GRSP + CET + CapFrameX.
3. Play the workload/scenario.
4. Press **F11** again to stop all three.
   - GRSP finalizes its capture here.
   - CapFrameX ends its capture here.
   - CET pauses/stops its measurement here.
5. Press **F12** to make CET write its CSV result set.
6. Close Cyberpunk 2077.
7. Return to TOTAL Profiler and click **COLLECT RESULTS**.
8. Click **COMPARE RESULTS**.

## Collection layout

A collected run becomes:

```text
Results/
  Capture_YYYYMMDD-HHMMSS_SCENARIO/
    CaptureManifest.json
    Raw/
      GRSP/
      CET/
      CapFrameX/
```

The collector copies the latest finalized GRSP capture, uses the CET manager's verified collect-and-clear path, and selects a valid CapFrameX JSON whose duration best matches the GRSP/CET measurement window.

The app performs a quick GRSP↔CET start/duration precheck before comparison.

## Comparison output

**COMPARE RESULTS** runs the bundled correlator into:

```text
Capture_.../
  Combined/
    GRSP_Combined_Report.html
    GRSP_Combined_Frames.csv
    GRSP_Combined_Timeline.csv
    GRSP_Combined_Hitches.csv
    GRSP_Analysis.json
    GRSP_AI_Analysis.md
    GRSP_Analysis_Package.zip
    GRSP_Correlator_Status.txt
```

It also creates:

```text
Capture_..._FULL.zip
```

beside the capture folder. That ZIP contains the raw GRSP/CET/CapFrameX inputs, the combined report, CSVs, AI-readable exports, and the capture manifest.

## CapFrameX compatibility

CapFrameX is **not version-locked**. The app tries to discover a portable capture path through `portable.json`, otherwise it uses the normal Documents capture directory as a default. The user can always override the result directory manually.

The correlator accepts CapFrameX JSONs containing the capture fields it needs. A future CapFrameX version that changes those fields may require an adapter update; the program should report an unsupported capture rather than rejecting a version number in advance.

## CET safety model

The bundled CET component preserves the exact `3.0.0-alpha6b` manager behavior:

- existing supported Scheduler integration is preserved;
- recognizable unintegrated/custom 0-Engine can receive the adaptive temporary bridge;
- **Core profiler only** leaves 0-Engine completely untouched;
- originals are backed up and verified before replacement;
- restore reverses only the files the manager changed;
- live CET CSVs are hash-verified before they are cleared.

The CET profiler payload still validates the installed CET ASI against the exact supported official/profiler hashes before changing it.

## GRSP installation

TOTAL Profiler installs only the GRSP runtime pieces required by the game:

```text
red4ext/plugins/redscript_profiler_alpha.dll
red4ext/plugins/redscript_profiler_alpha/RSP_Scenario.txt
```

If a different DLL already exists at the GRSP DLL path, TOTAL Profiler backs it up before replacement and can restore it later. GRSP result folders are never deleted by **Restore GRSP DLL**.

## Building the Windows EXE

The repository includes a GitHub Actions workflow. Upload the repository as-is and run/push the workflow. It builds:

```text
Gs-Cyberpunk2077-TOTAL-Profiler.exe
```

with all GRSP/CET/correlator components embedded. CapFrameX remains external.

For source execution on Windows with Python 3.10+:

```text
Run_TOTAL_Profiler.bat
```

## Prototype scope

This first unified build intentionally keeps the tested profiler engines unchanged. The main remaining UX limitation is CET's binding system: CET requires the user to assign F11/F12 once inside its Bindings UI. A later GRSP build can move the shared F11 setting into a common config if configurable capture keys are required.
