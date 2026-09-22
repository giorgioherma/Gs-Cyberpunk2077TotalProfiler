using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal sealed class AppConfig
{
    public string GameDirectory { get; set; } = "";
    public string CapFrameXExe { get; set; } = "";
    public string CapFrameXResults { get; set; } = "";
    public string ResultsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Results");
    public string Scenario { get; set; } = "TEST";
    public bool CetCoreOnly { get; set; }
    public string LastCapture { get; set; } = "";

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

            // v0.2.2 migration: only move the old untouched default. A user-selected
            // custom results directory is preserved exactly.
            var oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "G-Cyberpunk2077-TOTAL-Profiler", "Results");
            if (string.IsNullOrWhiteSpace(cfg.ResultsDirectory) ||
                string.Equals(Path.GetFullPath(cfg.ResultsDirectory), Path.GetFullPath(oldDefault), StringComparison.OrdinalIgnoreCase))
            {
                cfg.ResultsDirectory = Path.Combine(AppContext.BaseDirectory, "Results");
            }
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
