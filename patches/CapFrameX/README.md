# Bundled CapFrameX 1.9.0 tab-registration hotfix

TOTAL Profiler begins with the exact official CapFrameX 1.9.0 portable release and verifies the upstream archive SHA-256 before applying this hotfix.

CapFrameX 1.9.0 contains commit \`a09ab380ec7f148b2009d69f23d21e8ed327e242\` ("Defer tab view registration"). Its \`CapFrameXViewRegion.RegisterDeferred\` can leave CAPTURE/ANALYSIS/etc. unregistered after the shell becomes interactive. The visible symptom is that the selected tab changes but the main content remains on the previously viewed page.

The build-time patcher changes only that registration path: after CapFrameX obtains its Dispatcher, the local is forced to null so CapFrameX executes its own existing synchronous fallback registration loop.

No PresentMon, capture-engine, F11, portable-mode, sensor, or result-format logic is modified.

Every package includes \`Tools/CapFrameX/TOTALProfiler-CapFrameX-Patch.txt\` containing the original and patched CapFrameX.dll SHA-256 values.
