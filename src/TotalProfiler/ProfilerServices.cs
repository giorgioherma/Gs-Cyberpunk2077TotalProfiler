using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal static class ProfilerServices
{
    public const string AppName = "G's Cyberpunk 2077 TOTAL Profiler";
    public const string Version = "0.2.10";
    public const string GrspVersion = "0.5.0";
    public const string CetVersion = "3.0.0-alpha6b";
    public const string CorrelatorVersion = "0.2.0-native";
    public const string CaptureKey = "F11";
    public const string CetExportKey = "F12";
    public const string BundledCapFrameXVersion = "1.9.0";
    public const string GrspDllSha256 = "58b6caa3dccb03067d17a5d40ac049ba90cb74862b4302d7d2b5f91c5fca415d";

    public static string ComponentsDirectory => Path.Combine(AppContext.BaseDirectory, "components");
    public static string BundledCapFrameXExe => Path.Combine(AppContext.BaseDirectory, "Tools", "CapFrameX", "CapFrameX.exe");
    public static string BundledCapFrameXResults => Path.Combine(AppContext.BaseDirectory, "Tools", "CapFrameX", "Portable", "Captures");
    public static string DefaultResultsDirectory => Path.Combine(AppContext.BaseDirectory, "Results");

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("Cyberpunk2077").Length > 0; }
        catch { return false; }
    }

    public static (bool Ok, string Message) ValidateGameRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (false, "Folder does not exist.");
        var plugins = Path.Combine(root, "bin", "x64", "plugins");
        if (!Directory.Exists(plugins)) return (false, @"Missing bin\x64\plugins.");
        var exe = Path.Combine(root, "bin", "x64", "Cyberpunk2077.exe");
        if (!File.Exists(exe)) return (true, "Game plugins folder found; Cyberpunk2077.exe was not found at the normal path.");
        return (true, "Cyberpunk 2077 folder looks valid.");
    }

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string SafeName(string value)
    {
        var chars = value.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray();
        var s = new string(chars).Trim('.', '_');
        return string.IsNullOrWhiteSpace(s) ? "CAPTURE" : s;
    }

    public static void CopyTree(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    public static (string? Captures, string? Config) DetectCapFrameXPath(string exePath)
    {
        if (!File.Exists(exePath)) return (null, null);
        var baseDir = Path.GetDirectoryName(exePath)!;
        string? captures = null, config = null;
        var portable = Path.Combine(baseDir, "portable.json");
        if (File.Exists(portable))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(portable));
                if (doc.RootElement.TryGetProperty("paths", out var paths))
                {
                    if (paths.TryGetProperty("captures", out var c) && c.ValueKind == JsonValueKind.String)
                        captures = ResolveRelative(baseDir, c.GetString());
                    if (paths.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.String)
                        config = ResolveRelative(baseDir, cfg.GetString());
                }
            }
            catch { }
        }
        captures ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CapFrameX", "Captures");
        config ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapFrameX", "Configuration");
        return (captures, config);
    }

    private static string? ResolveRelative(string baseDir, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value));
    }

    public static string ConfigureCapFrameXF11BestEffort(string exePath)
    {
        var (_, cfg) = DetectCapFrameXPath(exePath);
        if (cfg is null || !Directory.Exists(cfg)) return "CapFrameX settings folder not found yet. Set Capture Hotkey to F11 in CapFrameX.";
        var candidates = Directory.EnumerateFiles(cfg, "*.json", SearchOption.TopDirectoryOnly).ToList();
        foreach (var path in candidates)
        {
            try
            {
                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                if (node is null) continue;
                bool changed = SetJsonKeyRecursive(node, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "capturehotkey", "capturehotkeystring" }, CaptureKey);
                if (!changed) continue;
                var backup = path + ".TOTALProfiler.bak";
                if (!File.Exists(backup)) File.Copy(path, backup);
                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                return $"CapFrameX capture hotkey set to F11. Backup: {Path.GetFileName(backup)}";
            }
            catch { }
        }
        return "CapFrameX capture-hotkey setting was not found. Set Capture Hotkey to F11 in CapFrameX.";
    }

    public static string RestoreCapFrameXConfigBestEffort(string exePath)
    {
        var (_, cfg) = DetectCapFrameXPath(exePath);
        if (cfg is null || !Directory.Exists(cfg)) return "No CapFrameX settings folder was found; nothing to restore.";

        int restored = 0;
        foreach (var backup in Directory.EnumerateFiles(cfg, "*.TOTALProfiler.bak", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var original = backup[..^".TOTALProfiler.bak".Length];
                File.Copy(backup, original, true);
                File.Delete(backup);
                restored++;
            }
            catch { }
        }

        return restored > 0
            ? $"Restored {restored} CapFrameX settings backup(s)."
            : "No TOTAL Profiler CapFrameX settings backup was present.";
    }

    private static bool SetJsonKeyRecursive(System.Text.Json.Nodes.JsonNode node, HashSet<string> names, string value)
    {
        bool changed = false;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                if (names.Contains(kv.Key))
                {
                    if (kv.Value?.ToString() != value) { obj[kv.Key] = value; changed = true; }
                }
                else if (kv.Value is not null) changed |= SetJsonKeyRecursive(kv.Value, names, value);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var x in arr) if (x is not null) changed |= SetJsonKeyRecursive(x, names, value);
        }
        return changed;
    }

    public static GrspInstallResult InstallGrsp(string gameRoot, string scenario)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Cyberpunk 2077 is running. Close the game before installing profilers.");
        var valid = ValidateGameRoot(gameRoot);
        if (!valid.Ok) throw new InvalidOperationException(valid.Message);

        var srcDll = Path.Combine(ComponentsDirectory, "grsp", "red4ext", "plugins", "redscript_profiler_alpha.dll");
        var srcScenario = Path.Combine(ComponentsDirectory, "grsp", "red4ext", "plugins", "redscript_profiler_alpha", "RSP_Scenario.txt");
        if (!File.Exists(srcDll)) throw new FileNotFoundException("Bundled GRSP DLL is missing.", srcDll);
        if (!string.Equals(Sha256(srcDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bundled GRSP DLL failed its package hash check.");

        var pluginDir = Path.Combine(gameRoot, "red4ext", "plugins");
        var targetDll = Path.Combine(pluginDir, "redscript_profiler_alpha.dll");
        var dataDir = Path.Combine(pluginDir, "redscript_profiler_alpha");
        var statePath = Path.Combine(pluginDir, ".gs_total_profiler_grsp_state.json");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(dataDir);

        string mode;
        GrspState state;
        if (File.Exists(statePath))
        {
            state = JsonSerializer.Deserialize<GrspState>(File.ReadAllText(statePath), JsonOpts) ?? throw new InvalidOperationException("GRSP managed-state file is invalid.");
            if (File.Exists(targetDll) && string.Equals(Sha256(targetDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase)) mode = "already-managed";
            else throw new InvalidOperationException("GRSP managed state exists but the installed DLL differs. Restore GRSP first.");
        }
        else
        {
            mode = "added";
            string? backup = null;
            string? originalHash = null;
            if (File.Exists(targetDll))
            {
                originalHash = Sha256(targetDll);
                if (string.Equals(originalHash, GrspDllSha256, StringComparison.OrdinalIgnoreCase)) mode = "preexisting-same";
                else
                {
                    mode = "replaced";
                    backup = Path.Combine(pluginDir, "redscript_profiler_alpha.TOTALProfiler.ORIGINAL.dll");
                    if (File.Exists(backup)) throw new InvalidOperationException($"GRSP backup already exists: {backup}");
                    File.Copy(targetDll, backup);
                }
            }
            state = new GrspState { Version = Version, Mode = mode, OriginalHash = originalHash ?? "", InstalledHash = GrspDllSha256, Backup = backup ?? "" };
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts) + Environment.NewLine);
        }

        File.Copy(srcDll, targetDll, true);
        if (!string.Equals(Sha256(targetDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("GRSP DLL install verification failed.");
        var scenarioPath = Path.Combine(dataDir, "RSP_Scenario.txt");
        if (!File.Exists(scenarioPath) && File.Exists(srcScenario)) File.Copy(srcScenario, scenarioPath);
        File.WriteAllText(scenarioPath, SafeName(scenario.Trim().ToUpperInvariant()) + Environment.NewLine);
        return new GrspInstallResult(mode, targetDll, scenarioPath);
    }

    public static string RestoreGrsp(string gameRoot)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Cyberpunk 2077 is running. Close the game before restoring GRSP.");
        var pluginDir = Path.Combine(gameRoot, "red4ext", "plugins");
        var targetDll = Path.Combine(pluginDir, "redscript_profiler_alpha.dll");
        var statePath = Path.Combine(pluginDir, ".gs_total_profiler_grsp_state.json");
        if (!File.Exists(statePath)) return "No TOTAL Profiler GRSP managed state was found.";
        var state = JsonSerializer.Deserialize<GrspState>(File.ReadAllText(statePath), JsonOpts) ?? throw new InvalidOperationException("GRSP managed-state file is invalid.");
        if (state.Mode == "replaced")
        {
            if (!File.Exists(state.Backup)) throw new InvalidOperationException("GRSP original DLL backup is missing.");
            File.Copy(state.Backup, targetDll, true);
            if (!string.IsNullOrWhiteSpace(state.OriginalHash) && !string.Equals(Sha256(targetDll), state.OriginalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GRSP original DLL restore verification failed.");
            File.Delete(state.Backup);
        }
        else if (state.Mode == "added" && File.Exists(targetDll) && string.Equals(Sha256(targetDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase)) File.Delete(targetDll);
        File.Delete(statePath);
        return "GRSP DLL managed state restored. Capture RESULTS were left untouched.";
    }

    public static async Task<JsonDocument> CallCetAsync(string action, string gameRoot, string? resultsRoot = null, bool coreOnly = false)
    {
        var script = Path.Combine(ComponentsDirectory, "cet", "CET_Manager_Core.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Bundled CET manager is missing.", script);
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Quote(script), "-Action", action, "-GameRoot", Quote(gameRoot) };
        if (!string.IsNullOrWhiteSpace(resultsRoot)) { args.Add("-ResultsRoot"); args.Add(Quote(resultsRoot!)); }
        if (coreOnly) args.Add("-CoreProfilerOnly");
        var psi = new ProcessStartInfo("powershell.exe", string.Join(' ', args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell CET manager.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0) throw new InvalidOperationException((string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse().FirstOrDefault(x => x.TrimStart().StartsWith('{'));
        if (line is null) throw new InvalidOperationException("CET manager did not return status JSON.");
        return JsonDocument.Parse(line);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static string? LatestGrspCapture(string gameRoot, DateTime? notBeforeUtc = null)
    {
        var results = Path.Combine(gameRoot, "red4ext", "plugins", "redscript_profiler_alpha", "RESULTS");
        if (!Directory.Exists(results)) return null;
        var latest = Path.Combine(results, "LATEST.txt");
        if (File.Exists(latest))
        {
            try
            {
                var raw = File.ReadAllText(latest).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var p = Path.IsPathRooted(raw) ? raw : Path.Combine(results, raw);
                    if (Directory.Exists(p) && File.Exists(Path.Combine(p, "GRSP_Summary.csv")) &&
                        (notBeforeUtc is null || Directory.GetLastWriteTimeUtc(p) >= notBeforeUtc.Value))
                        return Path.GetFullPath(p);
                }
            }
            catch { }
        }
        return Directory.EnumerateDirectories(results, "Capture_*", SearchOption.TopDirectoryOnly)
            .Where(p => File.Exists(Path.Combine(p, "GRSP_Summary.csv")))
            .Where(p => notBeforeUtc is null || Directory.GetLastWriteTimeUtc(p) >= notBeforeUtc.Value)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static CaptureMeta ReadGrspMeta(string capture)
    {
        var r = CsvUtil.First(Path.Combine(capture, "GRSP_Summary.csv"));
        return new CaptureMeta(CsvUtil.D(r, "start_unix_ms"), CsvUtil.D(r, "stop_unix_ms"), CsvUtil.D(r, "duration_ms"));
    }

    public static CaptureMeta ReadCetMeta(string dir)
    {
        var rows = CsvUtil.Read(Path.Combine(dir, "CET_Runtime_Profile_Markers.csv"));
        var start = rows.FirstOrDefault(r => string.Equals(CsvUtil.S(r, "Label"), "CAPTURE_START", StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        if (start is null) return new CaptureMeta(0, 0, 0);
        var startEpoch = CsvUtil.D(start, "UnixEpochMs");
        var startCap = CsvUtil.D(start, "CaptureMs");
        var maxCap = rows.Count == 0 ? 0 : rows.Max(r => CsvUtil.D(r, "CaptureMs"));
        return new CaptureMeta(startEpoch, startEpoch + (maxCap - startCap), maxCap - startCap);
    }

    public static double? CapXDurationMs(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;
            double? best = null;
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("CaptureData", out var cd) || !cd.TryGetProperty("TimeInSeconds", out var ts) || ts.ValueKind != JsonValueKind.Array || ts.GetArrayLength() == 0) continue;
                var dur = ts[ts.GetArrayLength() - 1].GetDouble() * 1000.0;
                if (best is null || dur > best) best = dur;
            }
            return best;
        }
        catch { return null; }
    }

    public static (string? Path, double? Duration) ChooseCapXCapture(string root, double targetDurationMs, DateTime? notBeforeUtc = null)
    {
        if (!Directory.Exists(root)) return (null, null);
        string? bestPath = null; double? bestDur = null; double bestScore = double.MaxValue;
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                     .Where(p => notBeforeUtc is null || File.GetLastWriteTimeUtc(p) >= notBeforeUtc.Value)
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            var d = CapXDurationMs(path);
            if (d is null) continue;
            var score = Math.Abs(d.Value - targetDurationMs);
            if (score < bestScore) { bestScore = score; bestPath = path; bestDur = d; }
        }
        return (bestPath, bestDur);
    }

    public static string? LatestValidCapXCapture(string root, DateTime? notBeforeUtc = null)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(p => notBeforeUtc is null || File.GetLastWriteTimeUtc(p) >= notBeforeUtc.Value)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(p => CapXDurationMs(p) is not null);
    }

    public static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void CreateDirectoryZip(string sourceDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, false);
    }

    public static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal sealed class GrspState
    {
        public string Version { get; set; } = "";
        public string Mode { get; set; } = "";
        public string OriginalHash { get; set; } = "";
        public string InstalledHash { get; set; } = "";
        public string Backup { get; set; } = "";
    }
}

internal sealed record GrspInstallResult(string Mode, string Dll, string ScenarioPath);
internal sealed record CaptureMeta(double StartUnixMs, double StopUnixMs, double DurationMs);
