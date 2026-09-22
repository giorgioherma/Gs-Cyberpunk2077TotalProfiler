using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal sealed class AppConfig
{
    public string GameDirectory { get; set; } = "";
    public string CapFrameXExe { get; set; } = "";
    public string CapFrameXResults { get; set; } = "";
    public bool UseBundledCapFrameX { get; set; } = true;
    public string ResultsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Results");
    public string Scenario { get; set; } = "TEST";
    public string CaptureName { get; set; } = "TEST";
    public bool CetCoreOnly { get; set; }
    public string LastCapture { get; set; } = "";
    public DateTimeOffset? CaptureResetUtc { get; set; }

    public static string StateDirectory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;
            var p = Path.Combine(baseDir, "GsCyberpunk2077_TOTAL_Profiler");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string ConfigPath => Path.Combine(StateDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new AppConfig();

            var newDefault = Path.Combine(AppContext.BaseDirectory, "Results");
            var oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "G-Cyberpunk2077-TOTAL-Profiler", "Results");
            var sameAsCapFrameX = !string.IsNullOrWhiteSpace(cfg.CapFrameXResults) &&
                                  !string.IsNullOrWhiteSpace(cfg.ResultsDirectory) &&
                                  string.Equals(Path.GetFullPath(cfg.ResultsDirectory), Path.GetFullPath(cfg.CapFrameXResults), StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cfg.ResultsDirectory) ||
                string.Equals(Path.GetFullPath(cfg.ResultsDirectory), Path.GetFullPath(oldDefault), StringComparison.OrdinalIgnoreCase) ||
                sameAsCapFrameX)
            {
                cfg.ResultsDirectory = newDefault;
            }

            if (string.IsNullOrWhiteSpace(cfg.CaptureName)) cfg.CaptureName = "TEST";
            if (string.IsNullOrWhiteSpace(cfg.Scenario)) cfg.Scenario = "TEST";
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
