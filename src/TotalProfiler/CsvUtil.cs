using System.Globalization;
using System.Text;

namespace GsCyberpunkTotalProfiler;

internal static class CsvUtil
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        using var sr = new StreamReader(path, Encoding.UTF8, true);
        var headerLine = sr.ReadLine();
        if (headerLine is null) return rows;
        var headers = ParseLine(headerLine);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var vals = ParseLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
                row[headers[i]] = i < vals.Count ? vals[i] : "";
            rows.Add(row);
        }
        return rows;
    }

    public static Dictionary<string, string> First(string path)
        => File.Exists(path) ? Read(path).FirstOrDefault() ?? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

    public static void Write(string path, IReadOnlyList<string> fields, IEnumerable<IDictionary<string, object?>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
        sw.WriteLine(string.Join(',', fields.Select(Escape)));
        foreach (var row in rows)
        {
            sw.WriteLine(string.Join(',', fields.Select(f => Escape(Format(row.TryGetValue(f, out var v) ? v : null)))));
        }
    }

    public static double D(Dictionary<string, string> row, string key, double fallback = 0)
    {
        if (!row.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s)) return fallback;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public static int I(Dictionary<string, string> row, string key, int fallback = 0)
    {
        var d = D(row, key, double.NaN);
        return double.IsNaN(d) ? fallback : (int)d;
    }

    public static string S(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var s) ? s : "";

    public static bool B(Dictionary<string, string> row, string key)
        => string.Equals(S(row, key), "true", StringComparison.OrdinalIgnoreCase) || S(row, key) == "1";

    private static List<string> ParseLine(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') quoted = true;
                else if (c == ',') { list.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        list.Add(sb.ToString());
        return list;
    }

    private static string Format(object? v)
    {
        if (v is null) return "";
        return v switch
        {
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "True" : "False",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static string Escape(string? s)
    {
        s ??= "";
        if (s.Contains('"')) s = s.Replace("\"", "\"\"");
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{s}\"" : s;
    }
}
