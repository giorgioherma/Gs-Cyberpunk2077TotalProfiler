using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal sealed record CollectResult(string CaptureDirectory, string SyncPrecheck, double? StartDeltaMs, double? DurationDeltaMs, string CapFrameXFile);

internal static class CaptureCollector
{
    public static async Task<CollectResult> CollectAsync(AppConfig cfg, Action<string>? log = null)
    {
        if (ProfilerServices.IsGameRunning()) throw new InvalidOperationException("Cyberpunk 2077 is still running. Close the game after F11/F12 before collecting results.");
        var valid = ProfilerServices.ValidateGameRoot(cfg.GameDirectory);
        if (!valid.Ok) throw new InvalidOperationException(valid.Message);
        if (!Directory.Exists(cfg.CapFrameXResults)) throw new DirectoryNotFoundException("CapFrameX results folder does not exist. Select it in Setup.");
        Directory.CreateDirectory(cfg.ResultsDirectory);

        var minUtc = cfg.CaptureResetUtc?.UtcDateTime;
        var grsp = ProfilerServices.LatestGrspCapture(cfg.GameDirectory, minUtc) ?? throw new InvalidOperationException("No GRSP capture was found after the last capture-state reset. Did you press F11 twice?");
        var gm = ProfilerServices.ReadGrspMeta(grsp);
        log?.Invoke($"GRSP found: {Path.GetFileName(grsp)} · {gm.DurationMs / 1000:F3}s");

        var staging = Path.Combine(cfg.ResultsDirectory, ".staging_cet");
        Directory.CreateDirectory(staging);
        using var cetJson = await ProfilerServices.CallCetAsync("Collect", cfg.GameDirectory, staging);
        if (!cetJson.RootElement.TryGetProperty("destination", out var destEl)) throw new InvalidOperationException("CET manager did not return a collection destination.");
        var cet = destEl.GetString() ?? "";
        if (!Directory.Exists(cet)) throw new InvalidOperationException("CET manager reported collection success but its archive folder was not found.");
        var cm = ProfilerServices.ReadCetMeta(cet);
        log?.Invoke($"CET found/exported: {Path.GetFileName(cet)} · {cm.DurationMs / 1000:F3}s");

        double target = gm.DurationMs;
        if (cm.DurationMs > 0 && target > 0) target = (target + cm.DurationMs) / 2.0;
        var cap = ProfilerServices.ChooseCapXCapture(cfg.CapFrameXResults, target, minUtc);
        if (cap.Path is null) throw new InvalidOperationException("No valid CapFrameX JSON capture was found in the configured results folder.");
        log?.Invoke($"CapFrameX chosen: {Path.GetFileName(cap.Path)} · {(cap.Duration ?? 0) / 1000:F3}s");

        var scenario = ProfilerServices.SafeName(cfg.Scenario.Trim().ToUpperInvariant());
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var capture = Path.Combine(cfg.ResultsDirectory, $"Capture_{stamp}_{scenario}");
        int suffix = 1;
        while (Directory.Exists(capture)) capture = Path.Combine(cfg.ResultsDirectory, $"Capture_{stamp}_{scenario}_{suffix++}");
        var raw = Path.Combine(capture, "Raw");
        var grspDst = Path.Combine(raw, "GRSP");
        var cetDst = Path.Combine(raw, "CET");
        var capDst = Path.Combine(raw, "CapFrameX");
        Directory.CreateDirectory(grspDst); Directory.CreateDirectory(cetDst); Directory.CreateDirectory(capDst);
        ProfilerServices.CopyTree(grsp, grspDst);
        ProfilerServices.CopyTree(cet, cetDst);
        File.Copy(cap.Path, Path.Combine(capDst, Path.GetFileName(cap.Path)), true);

        try
        {
            Directory.Delete(cet, true);
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
        }
        catch { }

        double? sd = gm.StartUnixMs > 0 && cm.StartUnixMs > 0 ? cm.StartUnixMs - gm.StartUnixMs : null;
        double? dd = gm.DurationMs > 0 && cm.DurationMs > 0 ? cm.DurationMs - gm.DurationMs : null;
        var sync = sd is null || Math.Abs(sd.Value) > 100 || (dd is not null && Math.Abs(dd.Value) > 500) ? "CHECK" : "GOOD";
        var manifest = new Dictionary<string, object?>
        {
            ["total_profiler_version"] = ProfilerServices.Version,
            ["created_local"] = DateTimeOffset.Now.ToString("O"),
            ["scenario"] = scenario,
            ["components"] = new Dictionary<string,string>
            {
                ["GRSP"] = ProfilerServices.GrspVersion,
                ["CET_Runtime_Profiler"] = ProfilerServices.CetVersion,
                ["CapFrameX"] = "external / version not locked",
                ["Correlator"] = ProfilerServices.CorrelatorVersion
            },
            ["source_paths"] = new Dictionary<string,string> { ["grsp"] = grsp, ["cet"] = cet, ["capframex"] = cap.Path },
            ["capture"] = new Dictionary<string,object?>
            {
                ["sync_precheck"] = sync,
                ["grsp_start_unix_ms"] = gm.StartUnixMs,
                ["cet_start_unix_ms"] = cm.StartUnixMs,
                ["grsp_cet_start_delta_ms"] = sd,
                ["grsp_duration_ms"] = gm.DurationMs,
                ["cet_duration_ms"] = cm.DurationMs,
                ["grsp_cet_duration_delta_ms"] = dd,
                ["capframex_duration_ms"] = cap.Duration
            }
        };
        File.WriteAllText(Path.Combine(capture, "CaptureManifest.json"), JsonSerializer.Serialize(manifest, ProfilerServices.JsonOpts) + Environment.NewLine);
        log?.Invoke($"Collected into: {capture}");
        if (sd is not null) log?.Invoke($"GRSP↔CET START delta: {sd:F3} ms");
        if (dd is not null) log?.Invoke($"GRSP↔CET duration delta: {dd:F3} ms");
        log?.Invoke($"Capture precheck: {sync}");
        return new CollectResult(capture, sync, sd, dd, cap.Path);
    }
}
