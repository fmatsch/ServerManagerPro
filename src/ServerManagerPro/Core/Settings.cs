using System.Text.Json;

namespace ServerManagerPro.Core;

public sealed class AppSettings
{
    public int RefreshSeconds { get; set; } = 2;
    public double CpuWarn { get; set; } = 85;
    public double CpuCrit { get; set; } = 95;
    public double MemWarn { get; set; } = 85;
    public double MemCrit { get; set; } = 95;
    public double DiskFreeWarnPct { get; set; } = 15;
    public double DiskFreeCritPct { get; set; } = 5;
    public int BackupMaxAgeHours { get; set; } = 36;
    public int EventWarnCount { get; set; } = 10;
    public int EventCritCount { get; set; } = 100;
    /// <summary>Dienste, die immer laufen müssen (Name oder Anzeigename).</summary>
    public List<string> WatchedServices { get; set; } = new();
    /// <summary>Zusätzliche Dienstnamen-Muster für Datenbanken (z. B. "MyDbService*").</summary>
    public List<string> ExtraDatabaseServices { get; set; } = new();
    /// <summary>Zusätzliche Stichwörter, an denen Backup-Aufgaben erkannt werden.</summary>
    public List<string> ExtraBackupKeywords { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServerManagerPro");
            try
            {
                System.IO.Directory.CreateDirectory(d);
                var probe = Path.Combine(d, ".w");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return d;
            }
            catch
            {
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerManagerPro");
                System.IO.Directory.CreateDirectory(d);
                return d;
            }
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}
