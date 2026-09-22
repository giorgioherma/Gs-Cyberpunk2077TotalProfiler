# CapFrameX Pass 13 Capture-tab fix

Target: CapFrameX 1.9.0 source, `source/CapFrameX/CapFrameXViewRegion.cs`.

Upstream regression source:
- commit `a09ab380ec7f148b2009d69f23d21e8ed327e242`
- message: `Defer tab view registration`

That change deferred `CaptureView` and `OverlayView` to `DispatcherPriority.ApplicationIdle`.
The CAPTURE navigation can therefore be used before `CaptureView` has been registered.

This fix restores eager registration for only:
- `CaptureView`
- `OverlayView`

All other non-critical views remain deferred, preserving the Pass 13 startup optimization.

Files:
- `PASS13_CAPTURE_TAB_FIX.patch` — minimal diff against the upstream Pass 13 implementation.
- `CapFrameXViewRegion.cs` — complete corrected replacement source file.

Important: the TOTAL Profiler build currently downloads the official prebuilt CapFrameX 1.9.0 portable release. Merely storing this patch in the repository does not modify that upstream binary. The patch must be applied when producing a custom CapFrameX build before it can affect the bundled executable.
