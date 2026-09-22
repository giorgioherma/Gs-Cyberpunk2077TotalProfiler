CET Runtime Profiler v3.0.0-alpha6b - ADAPTIVE 0-ENGINE / CORE MODE TEST
============================================================================

PURPOSE
-------
Alpha6b keeps the proven PASS6 architecture where 0-Engine owns Scheduler when the
user already has that integration. It also supports unknown/custom 0-Engine versions
without pretending that merely finding the folder proves compatibility.

IMPORTANT STATUS CHANGE
-----------------------
The Manager reports `FOUND - UNVERIFIED` for 0-Engine. It then decides which Scheduler
integration path is available.

THREE 0-ENGINE PATHS
--------------------
1) EXISTING PASS / SCHEDULER-INTEGRATED 0-ENGINE
   - init.lua stays byte-for-byte untouched.
   - the existing modules/Scheduler.lua is temporarily replaced only when needed.
   - restore puts the exact original Scheduler.lua back.

2) RECOGNIZED UNINTEGRATED / CUSTOM 0-ENGINE
   - the Manager copies and hashes the user's exact init.lua.
   - it identifies the user's final standalone `return Engine` export anchor.
   - it injects only the temporary Scheduler bridge into THEIR init.lua.
   - it adds `modules/CETProfilerScheduler.lua`, a unique profiler-owned module name.
   - it does NOT overwrite or collide with any unrelated custom `modules/Scheduler.lua`.
   - restore puts the exact original init.lua back and removes/restores the unique
     profiler module transactionally.

3) CORE PROFILER MODE - LEAVE 0-ENGINE UNTOUCHED
   Check `Core profiler only - leave 0-Engine untouched` if Scheduler integration fails
   or the user's 0-Engine structure is not recognized.

   In this mode:
   - 0-Engine remains in the CET mods folder.
   - 0-Engine loads normally exactly as the user installed it.
   - the Manager does NOT patch init.lua.
   - the Manager does NOT replace/add Scheduler.lua.
   - the Manager does NOT add CETProfilerScheduler.lua.
   - the native CET profiler ASI and CETProfilerControls are still installed.
   - normal/core CET profiling remains available.
   - Scheduler-specific attribution is available only if the user's existing setup
     already provides a profiler-aware Scheduler integration.

   This mode is intentionally a non-invasive fallback. It does not attempt to repair a
   broken 0-Engine. If 0-Engine itself fails, that failure remains visible in the user's
   CET logs while the core profiler can still run independently.

WHY CORE MODE DOES NOT REMOVE 0-ENGINE
--------------------------------------
Removing 0-Engine would also disable any optimization/client mods that depend on it,
which would change the runtime being measured. Core mode therefore leaves the user's
actual mod stack intact and merely skips our Scheduler integration.

ADAPTIVE SAFETY RULE
--------------------
The Manager does not blindly modify arbitrary Lua. Adaptive injection is offered only
when it can identify both an Engine table and a final standalone `return Engine`. If
that structure is not recognized, normal Scheduler integration is disabled and the UI
asks the user to choose Core profiler mode instead.

TRANSACTION RULE
----------------
Every original file that the Manager changes is backed up before replacement and
verified. Restore reverses only what the Manager changed. In Core profiler mode,
0-Engine is never part of the transaction because it is never modified.

CETProfilerControls remains temporary profiler-owned UI and is always deleted on
restore regardless of key assignments.

RESULTS / REFRESH
-----------------
RESULTS stay beside the Profiler Manager package. Live CSVs are copied, SHA256-verified
and then cleared. Status refresh is event-driven on window activation plus a manual
REFRESH STATUS button; there is no polling timer.

ALPHA6B TEST ORDER
------------------
A. Known PASS6 install
   1. Leave the known-good PASS6 0-Engine + Scheduler state installed.
   2. Confirm `FOUND - UNVERIFIED` + `EXISTING SCHEDULER INTEGRATION`.
   3. Install, profile, export, collect, restore.
   4. Confirm init.lua was untouched and Scheduler.lua restored exactly.

B. Adaptive custom/unintegrated 0-Engine
   1. Use a clean/unintegrated 0-Engine copy with a recognizable `return Engine`.
   2. Confirm `ADAPTIVE BRIDGE AVAILABLE`.
   3. Install and confirm `modules/CETProfilerScheduler.lua` was added while any
      existing `modules/Scheduler.lua` was left alone.
   4. Restore and confirm init.lua and module state are exact originals.

C. Core profiler fallback
   1. Record hashes/fingerprint of the user's 0-Engine folder.
   2. Check `Core profiler only - leave 0-Engine untouched`.
   3. Install and confirm the 0-Engine folder remains exactly where it was.
   4. Run/export profiler data. Scheduler CSVs may be empty unless the existing
      Scheduler already has profiler attribution support.
   5. Restore and confirm 0-Engine was never changed at all.
