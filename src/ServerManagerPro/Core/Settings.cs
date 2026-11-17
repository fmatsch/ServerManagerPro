using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Optionale Verbindungen für Detailwerte (eine pro Zeile): "postgresql|Dienstname-Teil|Host=localhost;Username=monitor;Password=…"
    /// oder "mysql|…|Server=localhost;User ID=monitor;Password=…". Im Speicher im Klartext, in der Datei mit Windows-DPAPI verschlüsselt.
    /// </summary>
    [JsonIgnore] public List<string> DbConnections { get; set; } = new();

    public string DbConnectionsProtected
    {
        get
        {
            if (DbConnections.Count == 0) return "";
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(string.Join("\n", DbConnections)), null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(enc);
        }
        set
        {
            try
            {
                if (string.IsNullOrEmpty(value)) { DbConnections = new(); return; }
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine);
                DbConnections = Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch { DbConnections = new(); }
        }
    }

    /// <summary>auto, de oder en</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Beim Start und täglich bei GitHub nach einer neuen Version fragen (aus Datenschutzgründen standardmäßig aus).</summary>
    public bool CheckUpdates { get; set; }

    // ---- Alarme ----
    public bool AlertsEnabled { get; set; }
    /// <summary>Ein Problem muss so lange bestehen, bevor gemeldet wird (verhindert Fehlalarme bei kurzen Spitzen).</summary>
    public int AlertDelayMinutes { get; set; } = 3;
    /// <summary>Erinnerung, solange das Problem besteht (0 = nie).</summary>
    public int AlertReminderHours { get; set; } = 12;
    public bool AlertOnlyCritical { get; set; }
    public bool AlertSendRecovery { get; set; } = true;
    public int AlertMaxPerHour { get; set; } = 20;
    /// <summary>
    /// Ziele, eine pro Zeile: ntfy|https://ntfy.sh/Thema|Token (optional) · teams|Webhook-URL · slack|Webhook-URL ·
    /// telegram|BotToken|ChatId · webhook|URL (JSON) · tray. Enthält Geheimnisse, daher in der Datei DPAPI-verschlüsselt.
    /// </summary>
    [JsonIgnore] public List<string> AlertTargets { get; set; } = new();
    public string AlertTargetsProtected { get => ProtectLines(AlertTargets); set => AlertTargets = UnprotectLines(value); }

    // ---- Status-Zugriff (schreibgeschützt) und entfernte Server ----
    public bool ServeEnabled { get; set; }
    /// <summary>Adresse, an der gelauscht wird. 127.0.0.1 = nur dieser Rechner; die Adresse des VPN-Adapters für Zugriff über ein VPN.</summary>
    public string ServeBind { get; set; } = "127.0.0.1";
    public int ServePort { get; set; } = 8765;
    [JsonIgnore] public string ServeToken { get; set; } = "";
    public string ServeTokenProtected { get => ProtectLines(new() { ServeToken }); set => ServeToken = UnprotectLines(value).FirstOrDefault() ?? ""; }
    /// <summary>Entfernte Server, eine pro Zeile: Name|https://adresse:8765|Token|Fingerabdruck (SHA-1 des Zertifikats, optional).</summary>
    [JsonIgnore] public List<string> RemoteServers { get; set; } = new();
    public string RemoteServersProtected { get => ProtectLines(RemoteServers); set => RemoteServers = UnprotectLines(value); }

    static string ProtectLines(List<string> lines)
    {
        var l = lines.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (l.Count == 0) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(string.Join("\n", l)), null, DataProtectionScope.LocalMachine));
    }

    static List<string> UnprotectLines(string value)
    {
        try
        {
            if (string.IsNullOrEmpty(value)) return new();
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch { return new(); }
    }

    // ---- Overlay ----
    public bool OverlayOnStart { get; set; }
    /// <summary>cpu, ram, disk, net, health, free, topproc, dbconn, rdp, smb, inet, backup, dhcp, ad</summary>
    public List<string> OverlayMetrics { get; set; } = new() { "health", "cpu", "ram", "disk", "net" };
    /// <summary>TopRight, TopLeft, BottomRight, BottomLeft oder Custom (frei verschoben).</summary>
    public string OverlayCorner { get; set; } = "TopRight";
    /// <summary>0 = Hauptbildschirm, sonst Nummer des Bildschirms (1-basiert, wie in der Auswahl).</summary>
    public int OverlayMonitor { get; set; }
    public double OverlayX { get; set; }
    public double OverlayY { get; set; }
    public bool OverlayHorizontal { get; set; }
    public double OverlayOpacity { get; set; } = 0.85;
    public double OverlayFont { get; set; } = 13;

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
