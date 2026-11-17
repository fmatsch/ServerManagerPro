using System.Globalization;
using System.Text;

namespace ServerManagerPro.Core;

/// <summary>Ein Messpunkt (Minutenmittel). NaN = nicht gemessen.</summary>
public sealed record Sample(DateTime T, double CpuAvg, double CpuMax, double MemAvg, double MemMax, double DiskAvg, double DiskMax,
    double NetRx, double NetTx, double Score, int Overall, double Conn, double Rdp);

/// <summary>Eine Zustandsänderung einer Prüfung.</summary>
public sealed class ChangeRec
{
    public DateTime Time { get; set; }
    public string Source { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Change => $"{Loc.T(From)} → {Loc.T(To)}";
}

/// <summary>
/// Einfache lokale Ablage ohne Datenbank: je Quelle und Tag eine CSV-Datei mit Minutenwerten, je Quelle eine Datei mit
/// Zustandswechseln. Ältere Tage werden nach der eingestellten Aufbewahrungszeit gelöscht.
/// </summary>
public static class HistoryStore
{
    static string? _root;
    static readonly object Gate = new();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Ordner der Ablage (für Tests änderbar).</summary>
    public static string Root
    {
        get => _root ??= Path.Combine(AppSettings.Directory, "history");
        set => _root = value;
    }

    public static string SourceKey(string source)
    {
        var sb = new StringBuilder();
        foreach (char c in source) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    static string MetricsFile(string source, DateTime day) => Path.Combine(Root, $"m-{SourceKey(source)}-{day:yyyyMMdd}.csv");
    static string ChangesFile(string source) => Path.Combine(Root, $"c-{SourceKey(source)}.csv");

    static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime;
    static string N(double d) => double.IsNaN(d) ? "" : Math.Round(d, 2).ToString("0.##", Inv);
    static double P(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : double.NaN;

    public static void Append(string source, Sample s)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                string line = string.Join(';', Unix(s.T), N(s.CpuAvg), N(s.CpuMax), N(s.MemAvg), N(s.MemMax), N(s.DiskAvg), N(s.DiskMax), N(s.NetRx), N(s.NetTx), N(s.Score), s.Overall, N(s.Conn), N(s.Rdp));
                File.AppendAllText(MetricsFile(source, s.T.Date), line + "\n", Encoding.UTF8);
            }
        }
        catch { /* Verlauf ist nie wichtiger als der Betrieb */ }
    }

    public static void AppendChange(ChangeRec c)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                string Clean(string x) => x.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(ChangesFile(c.Source), string.Join(';', Unix(c.Time), Clean(c.From), Clean(c.To), Clean(c.Category), Clean(c.Name), Clean(c.Detail)) + "\n", Encoding.UTF8);
            }
        }
        catch { }
    }

    static IEnumerable<string> ReadLines(string file)
    {
        if (!File.Exists(file)) yield break;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new StreamReader(fs, Encoding.UTF8);
        string? l;
        while ((l = rd.ReadLine()) != null) yield return l;
    }

    static Sample? Parse(string line)
    {
        var p = line.Split(';');
        if (p.Length < 13 || !long.TryParse(p[0], out var ts)) return null;
        return new Sample(FromUnix(ts), P(p[1]), P(p[2]), P(p[3]), P(p[4]), P(p[5]), P(p[6]), P(p[7]), P(p[8]), P(p[9]), int.TryParse(p[10], out var o) ? o : 0, P(p[11]), P(p[12]));
    }

    /// <summary>Alle Messpunkte im Zeitraum (roh, Minutenwerte).</summary>
    public static List<Sample> ReadRaw(string source, DateTime from, DateTime to)
    {
        var res = new List<Sample>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            foreach (var line in ReadLines(MetricsFile(source, d)))
            {
                var s = Parse(line);
                if (s != null && s.T >= from && s.T <= to) res.Add(s);
            }
        return res;
    }

    /// <summary>Messpunkte, auf höchstens <paramref name="maxPoints"/> Punkte zusammengefasst (Mittel der Mittelwerte, Höchstwert der Höchstwerte).</summary>
    public static List<Sample> Query(string source, DateTime from, DateTime to, int maxPoints, out int rawCount, out int[] overallCounts)
    {
        var raw = ReadRaw(source, from, to);
        rawCount = raw.Count;
        overallCounts = new[] { raw.Count(x => x.Overall == 0), raw.Count(x => x.Overall == 1), raw.Count(x => x.Overall >= 2) };
        if (raw.Count <= maxPoints) return raw;
        double bucket = Math.Max(60, Math.Ceiling((to - from).TotalSeconds / maxPoints));
        return raw.GroupBy(s => (long)((s.T - from).TotalSeconds / bucket)).OrderBy(g => g.Key).Select(g =>
        {
            double Avg(Func<Sample, double> f) { var v = g.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Average(); }
            double Max(Func<Sample, double> f) { var v = g.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Max(); }
            return new Sample(g.First().T.AddSeconds((g.Last().T - g.First().T).TotalSeconds / 2),
                Avg(s => s.CpuAvg), Max(s => s.CpuMax), Avg(s => s.MemAvg), Max(s => s.MemMax), Avg(s => s.DiskAvg), Max(s => s.DiskMax),
                Avg(s => s.NetRx), Avg(s => s.NetTx), Avg(s => s.Score), g.Max(s => s.Overall), Avg(s => s.Conn), Avg(s => s.Rdp));
        }).ToList();
    }

    public static List<ChangeRec> ReadChanges(string source, DateTime from, DateTime to, int max = 500)
    {
        var res = new List<ChangeRec>();
        foreach (var line in ReadLines(ChangesFile(source)))
        {
            var p = line.Split(';');
            if (p.Length < 6 || !long.TryParse(p[0], out var ts)) continue;
            var t = FromUnix(ts);
            if (t < from || t > to) continue;
            res.Add(new ChangeRec { Time = t, Source = source, From = p[1], To = p[2], Category = p[3], Name = p[4], Detail = p[5] });
        }
        return res.OrderByDescending(c => c.Time).Take(max).ToList();
    }

    /// <summary>Löscht Tagesdateien und Wechsel, die älter als die Aufbewahrungszeit sind.</summary>
    public static int Purge(int keepDays)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            var limit = DateTime.Now.Date.AddDays(-Math.Max(1, keepDays));
            lock (Gate)
            {
                foreach (var f in Directory.GetFiles(Root, "m-*-*.csv"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string day = name[(name.LastIndexOf('-') + 1)..];
                    if (DateTime.TryParseExact(day, "yyyyMMdd", Inv, DateTimeStyles.None, out var d) && d < limit) { File.Delete(f); removed++; }
                }
                foreach (var f in Directory.GetFiles(Root, "c-*.csv"))
                {
                    var keep = ReadLines(f).Where(l => long.TryParse(l.Split(';')[0], out var ts) && FromUnix(ts) >= limit).ToList();
                    int total = ReadLines(f).Count();
                    if (keep.Count < total) { File.WriteAllLines(f, keep, Encoding.UTF8); removed += total - keep.Count; }
                }
            }
        }
        catch { }
        return removed;
    }

    public static (DateTime? first, long bytes) Info(string source)
    {
        try
        {
            if (!Directory.Exists(Root)) return (null, 0);
            string key = SourceKey(source);
            var files = Directory.GetFiles(Root, $"m-{key}-*.csv");
            DateTime? first = null;
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "yyyyMMdd", Inv, DateTimeStyles.None, out var d) && (first == null || d < first)) first = d;
            }
            return (first, files.Sum(f => new FileInfo(f).Length));
        }
        catch { return (null, 0); }
    }

    public static void Clear()
    {
        try { lock (Gate) if (Directory.Exists(Root)) foreach (var f in Directory.GetFiles(Root, "*.csv")) File.Delete(f); } catch { }
    }
}
