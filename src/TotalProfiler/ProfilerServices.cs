using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal static class ProfilerServices
{
    public const string AppName = "G's Cyberpunk 2077 TOTAL Profiler";
    public const string Version = "0.2.20";
    public const string GrspVersion = "0.5.0";
    public const string CetVersion = "3.0.0-alpha6c";
    public const string CorrelatorVersion = "0.2.1-native";
    public const string CaptureKey = "F11";
    public const string CetExportKey = "F11 STOP + AUTO EXPORT";
    public const string BundledCapFrameXVersion = "1.9.1.2 Beta";
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

    public static string DirectoryFingerprint(string root)
    {
        if (!Directory.Exists(root)) return "";
        var rows = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Path: path))
            .OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Relative, StringComparer.Ordinal)
            .Select(x => x.Relative + "\0" + Sha256(x.Path));
        var material = string.Join("\n", rows);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
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
        var exe = Path.GetFullPath(exePath);
        var bundled = string.Equals(exe, Path.GetFullPath(BundledCapFrameXExe), StringComparison.OrdinalIgnoreCase);

        string cfg;
        if (bundled)
        {
            var capRoot = Path.GetDirectoryName(exe)!;
            cfg = Path.Combine(capRoot, "Portable", "Config");
            Directory.CreateDirectory(cfg);
            Directory.CreateDirectory(Path.Combine(capRoot, "Portable", "Captures"));
            Directory.CreateDirectory(Path.Combine(capRoot, "Portable", "Screenshots"));
            Directory.CreateDirectory(Path.Combine(capRoot, "Portable", "Logs"));
            Directory.CreateDirectory(Path.Combine(capRoot, "Portable", "Captures", "Cloud"));

            // CapFrameX only enters portable mode when portable.json exists beside CapFrameX.exe.
            // Without this file it silently uses AppData/Documents, which defeats our bundled layout.
            var portablePath = Path.Combine(capRoot, "portable.json");
            var portableJson = new
            {
                portable = true,
                paths = new
                {
                    config = "./Portable/Config",
                    captures = "./Portable/Captures",
                    screenshots = "./Portable/Screenshots",
                    logs = "./Portable/Logs",
                    cloud = "./Portable/Captures/Cloud"
                }
            };
            File.WriteAllText(portablePath, JsonSerializer.Serialize(portableJson, JsonOpts) + Environment.NewLine);
        }
        else
        {
            var detected = DetectCapFrameXPath(exe);
            if (detected.Config is null) return "CapFrameX settings folder could not be determined.";
            cfg = detected.Config;
            Directory.CreateDirectory(cfg);
        }

        var path = Path.Combine(cfg, "AppSettings.json");
        try
        {
            System.Text.Json.Nodes.JsonObject obj;
            var backup = path + ".TOTALProfiler.bak";

            if (File.Exists(path))
            {
                try
                {
                    var parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                    obj = parsed as System.Text.Json.Nodes.JsonObject
                          ?? throw new InvalidOperationException("AppSettings.json root is not an object.");
                }
                catch (JsonException) when (bundled)
                {
                    // v0.2.19 briefly emitted an invalid replacement token ("$10.0")
                    // while forcing CapFrameX's Double literals. A first failed install
                    // normally left the original seeded JSON in .TOTALProfiler.bak.
                    // Recover that backup automatically; if it is absent/unreadable,
                    // rebuild the bundled file from a minimal known-good object.
                    System.Text.Json.Nodes.JsonObject? recovered = null;
                    if (File.Exists(backup))
                    {
                        try
                        {
                            recovered = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(backup))
                                        as System.Text.Json.Nodes.JsonObject;
                        }
                        catch { }
                    }

                    obj = recovered ?? new System.Text.Json.Nodes.JsonObject();
                }

                if (!File.Exists(backup))
                {
                    // Only back up a parseable original. Never preserve the known-bad
                    // v0.2.19 malformed JSON as the restoration source.
                    var currentText = File.ReadAllText(path);
                    try
                    {
                        _ = System.Text.Json.Nodes.JsonNode.Parse(currentText);
                        File.WriteAllText(backup, currentText);
                    }
                    catch (JsonException) when (bundled)
                    {
                        // Bundled malformed file is a TOTAL Profiler regression and is
                        // intentionally replaced below.
                    }
                }
            }
            else
            {
                obj = new System.Text.Json.Nodes.JsonObject();
            }

            // Touch only the three settings TOTAL Profiler actually requires.
            // Everything else remains CapFrameX-owned/defaulted.
            obj["CaptureHotKey"] = CaptureKey;
            obj["CaptureTime"] = 0.0;
            obj["CaptureDelay"] = 0.0;

            // CapFrameX 1.9.1 beta strictly expects CaptureTime/CaptureDelay to deserialize
            // as CLR Double. System.Text.Json can serialize 0.0 as the integer-looking
            // literal 0, so normalize ONLY those two numeric literals after serialization.
            //
            // IMPORTANT: use a MatchEvaluator. The old replacement string "$10.0" was
            // ambiguous to Regex.Replace and could literally write "$10.0" into JSON.
            var capJson = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            capJson = System.Text.RegularExpressions.Regex.Replace(
                capJson,
                "(\\\"CaptureTime\\\"\\s*:\\s*)0(?=\\s*[,}])",
                m => m.Groups[1].Value + "0.0");
            capJson = System.Text.RegularExpressions.Regex.Replace(
                capJson,
                "(\\\"CaptureDelay\\\"\\s*:\\s*)0(?=\\s*[,}])",
                m => m.Groups[1].Value + "0.0");

            // Refuse to write anything CapFrameX itself cannot parse.
            using (JsonDocument.Parse(capJson)) { }
            File.WriteAllText(path, capJson + Environment.NewLine);

            return bundled
                ? "Bundled CapFrameX portable mode verified: F11 · unlimited capture (0 s) · Portable/Captures."
                : "Linked CapFrameX configured: F11 · unlimited capture (0 s).";
        }
        catch (Exception ex)
        {
            return "CapFrameX automatic configuration failed: " + ex.Message;
        }
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
        var backupDataDir = Path.Combine(pluginDir, ".gs_total_profiler_grsp_data_ORIGINAL");
        Directory.CreateDirectory(pluginDir);

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

            if (Directory.Exists(backupDataDir))
                throw new InvalidOperationException($"GRSP data backup already exists: {backupDataDir}");

            var dataExistedBefore = Directory.Exists(dataDir);
            var originalDataFingerprint = "";
            if (dataExistedBefore)
            {
                originalDataFingerprint = DirectoryFingerprint(dataDir);
                CopyTree(dataDir, backupDataDir);
                if (!string.Equals(DirectoryFingerprint(backupDataDir), originalDataFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GRSP data directory backup verification failed.");
            }

            state = new GrspState
            {
                Version = Version,
                Mode = mode,
                OriginalHash = originalHash ?? "",
                InstalledHash = GrspDllSha256,
                Backup = backup ?? "",
                DataStateTracked = true,
                DataDirExistedBefore = dataExistedBefore,
                BackupDataDir = dataExistedBefore ? backupDataDir : "",
                OriginalDataFingerprint = originalDataFingerprint
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts) + Environment.NewLine);
        }

        File.Copy(srcDll, targetDll, true);
        if (!string.Equals(Sha256(targetDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GRSP DLL install verification failed.");

        Directory.CreateDirectory(dataDir);
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
        var dataDir = Path.Combine(pluginDir, "redscript_profiler_alpha");
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
        else if (state.Mode == "added" && File.Exists(targetDll) &&
                 string.Equals(Sha256(targetDll), GrspDllSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(targetDll);
        }

        if (state.DataStateTracked)
        {
            if (state.DataDirExistedBefore)
            {
                if (string.IsNullOrWhiteSpace(state.BackupDataDir) || !Directory.Exists(state.BackupDataDir))
                    throw new InvalidOperationException("GRSP original data directory backup is missing.");
                if (!string.IsNullOrWhiteSpace(state.OriginalDataFingerprint) &&
                    !string.Equals(DirectoryFingerprint(state.BackupDataDir), state.OriginalDataFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GRSP original data directory backup fingerprint is wrong.");

                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                CopyTree(state.BackupDataDir, dataDir);

                if (!string.IsNullOrWhiteSpace(state.OriginalDataFingerprint) &&
                    !string.Equals(DirectoryFingerprint(dataDir), state.OriginalDataFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GRSP data directory restoration failed verification.");

                Directory.Delete(state.BackupDataDir, true);
            }
            else if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, true);
            }
        }
        else
        {
            // Legacy v0.2.18 state did not snapshot the GRSP data directory. Remove only
            // profiler output that is unquestionably ours; do not overwrite unknown data.
            var results = Path.Combine(dataDir, "RESULTS");
            if (Directory.Exists(results)) Directory.Delete(results, true);
            if (state.Mode == "added" && Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        }

        File.Delete(statePath);
        return state.DataStateTracked
            ? "GRSP DLL and data directory restored exactly to the pre-profiler state."
            : "GRSP DLL restored; legacy profiler RESULTS cleaned.";
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
        if (p.ExitCode != 0) throw new InvalidOperationException(CleanPowerShellError(stdout, stderr));
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse().FirstOrDefault(x => x.TrimStart().StartsWith('{'));
        if (line is null) throw new InvalidOperationException("CET manager did not return status JSON.");
        return JsonDocument.Parse(line);
    }

    private static string CleanPowerShellError(string stdout, string stderr)
    {
        var raw = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Where(x => !x.StartsWith("At ", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("+ CategoryInfo", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("+ FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("+ ", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0) return raw.Trim();
        var first = lines[0];
        var colon = first.IndexOf(": ", StringComparison.Ordinal);
        if (colon >= 0 && first[..colon].Contains(".ps1", StringComparison.OrdinalIgnoreCase))
            first = first[(colon + 2)..].Trim();
        return first;
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
        public bool DataStateTracked { get; set; }
        public bool DataDirExistedBefore { get; set; }
        public string BackupDataDir { get; set; } = "";
        public string OriginalDataFingerprint { get; set; } = "";
    }
}

internal sealed record GrspInstallResult(string Mode, string Dll, string ScenarioPath);
internal sealed record CaptureMeta(double StartUnixMs, double StopUnixMs, double DurationMs);
