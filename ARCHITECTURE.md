# TOTAL Profiler architecture

```text
TOTAL Profiler GUI
  |
  +-- bundled GRSP 0.5.0 installer
  |     -> <game>/red4ext/plugins/...
  |
  +-- bundled CET profiler 3.0.0-alpha6b
  |     -> command wrapper around the existing transaction-safe manager core
  |     -> <game>/bin/x64/plugins/...
  |
  +-- external CapFrameX link
  |     -> executable path
  |     -> capture/results path
  |
  +-- Results/<capture>/Raw
  |     +-- GRSP
  |     +-- CET
  |     +-- CapFrameX
  |
  +-- bundled correlator 0.1.2
        -> Results/<capture>/Combined
```

## Control semantics

```text
F11 #1  START
  GRSP START
  CET START / RESUME
  CapFrameX START

F11 #2  STOP
  GRSP STOP + FINALIZE
  CET PAUSE
  CapFrameX STOP

F12     CET CREATE CSV only
```

## Result ownership

Source profilers keep their native/live locations. TOTAL Profiler copies/archives the matching run into its configured Results root. That folder is the canonical combined project history.
