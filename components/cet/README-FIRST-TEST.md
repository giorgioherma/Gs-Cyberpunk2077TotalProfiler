CET Runtime Profiler v3.0.0-alpha6c
=====================================

Standalone CET Runtime Profiler for Cyberpunk 2077.

This standalone package is also the CET dependency consumed by G's Cyberpunk 2077 TOTAL Profiler. The profiler payload, install/restore engine, persistent state and result collection behavior are the same in both uses.

CAPTURE
-------

The Manager configures one CET input to F11 by default:

F11 #1 -> START a fresh measurement  
F11 #2 -> STOP the measurement and automatically export CSV results

There is no separate CREATE CSV / F12 action.

The profiler-owned CET binding state is stored with the persistent install transaction in:

bin\x64\plugins\.cet_runtime_profiler\

That state survives closing or restarting the standalone Manager or TOTAL Profiler. Restore returns the previous CETProfilerControls binding state as well as the managed CET / 0-Engine files.

RESULTS
-------

The native profiler writes its live CSVs in its normal CET location. The standalone Manager's COLLECT RESULTS / CLEAR LIVE action copies and SHA256-verifies them into this package's own:

RESULTS\

and only then clears the live CSVs.

TOTAL Profiler does not redirect this standalone result path. For a combined capture, TOTAL calls the same Collect action and then copies the completed CET result together with the GRSP and CapFrameX outputs into TOTAL's own result package. Combined-capture behavior belongs to TOTAL; standalone profiler behavior remains unchanged.

0-ENGINE MODES
--------------

1. Existing Scheduler-integrated 0-Engine
   - init.lua stays untouched.
   - Scheduler.lua is temporarily replaced only when required.
   - restore puts the exact original back.

2. Recognized unintegrated/custom 0-Engine
   - the exact init.lua is backed up.
   - a temporary profiler bridge is injected before the final return Engine.
   - modules\CETProfilerScheduler.lua is added under a profiler-owned name.
   - restore returns the exact original init.lua/module state.

3. Core profiler only
   - 0-Engine is left completely untouched.
   - native CET profiling remains available.
   - Scheduler attribution is available only if the existing setup already supports it.

SHARED CORE
-----------

CET_Manager_Core.ps1 is the authoritative lifecycle engine. ProfilerManager.ps1 is only the standalone GUI front-end.

The core supports:

- Status
- Install
- Collect
- ResetLive
- Restore

TOTAL Profiler calls this exact same core. There is no separate TOTAL-only implementation of CET install, 0-Engine injection, binding management, collection or restore.

RESTORE SAFETY
--------------

Original files are backed up before replacement and verified. Restore only reverses files managed by the profiler transaction. If live profiler CSVs still exist when Restore is requested, standalone CET archives them into its own RESULTS folder before restoring the game files.
