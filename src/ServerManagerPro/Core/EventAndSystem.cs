using System.Diagnostics.Eventing.Reader;
using System.Management;
using Microsoft.Win32;

namespace ServerManagerPro.Core;

/// <summary>Fehler und Warnungen der letzten 24 Stunden aus System- und Anwendungsprotokoll.</summary>
public static class EventCollector
{
    public static (List<EventRow> rows, List<CheckResult> checks) Collect(AppSettings cfg)
    {
        var all = new List<EventRow>();
        var checks = new List<CheckResult>();
        const string cat = "Ereignisse";
        foreach (var log in new[] { "System", "Application" })
        {
            int errors = 0, warns = 0;
            try
            {
                var q = new EventLogQuery(log, PathType.LogName,
                    "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 86400000]]]")
                { ReverseDirection = true };
                using var r = new EventLogReader(q);
                int shown = 0;
                for (int i = 0; i < 3000; i++)
                {
                    using var e = r.ReadEvent();
                    if (e == null) break;
                    bool err = e.Level is 1 or 2;
                    if (err) errors++; else warns++;
                    if (shown < 80)
                    {
                        shown++;
                        string msg = "";
                        try { msg = e.FormatDescription() ?? ""; } catch { }
                        msg = msg.Replace("\r", " ").Replace("\n", " ");
                        if (msg.Length > 300) msg = msg[..300] + " …";
                        all.Add(new EventRow
                        {
                            Time = e.TimeCreated ?? DateTime.Now,
                            Log = log,
                            Level = e.Level == 1 ? "Kritisch" : e.Level == 2 ? "Fehler" : "Warnung",
                            Source = e.ProviderName ?? "",
                            Id = e.Id,
                            Message = msg,
                        });
                    }
                }
                var st = errors >= cfg.EventCritCount ? Health.Critical : errors >= cfg.EventWarnCount ? Health.Warning : Health.Ok;
                checks.Add(new CheckResult(cat, $"{(log == "System" ? "System" : "Anwendungen")}-Protokoll", st,
                    $"{errors} Fehler und {warns} Warnungen in den letzten 24 Stunden."));
            }
            catch (Exception ex)
            {
                checks.Add(new CheckResult(cat, $"{log}-Protokoll", Health.Unknown, "Nicht lesbar: " + ex.Message));
            }
        }
        return (all.OrderByDescending(e => e.Time).Take(150).ToList(), checks);
    }
}

public static class SystemChecker
{
    public static (List<CheckResult> checks, List<DiskRow> disks) Collect(AppSettings cfg)
    {
        const string cat = "System";
        var checks = new List<CheckResult>();
        var disks = new List<DiskRow>();

        // Laufzeit
        var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
        checks.Add(new CheckResult(cat, "Laufzeit", up.TotalDays > 180 ? Health.Warning : Health.Ok,
            $"{(int)up.TotalDays} Tage {up.Hours} Std. seit dem letzten Start" + (up.TotalDays > 180 ? " – ein Neustart für Updates ist überfällig." : ".")));

        // Neustart ausstehend
        try
        {
            bool cbs = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            bool wu = Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            checks.Add(new CheckResult(cat, "Neustart ausstehend", cbs || wu ? Health.Warning : Health.Ok,
                cbs || wu ? "Windows wartet auf einen Neustart (Updates)." : "Kein Neustart erforderlich."));
        }
        catch { }

        // Laufwerke
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            try
            {
                var row = new DiskRow { Name = d.Name.TrimEnd('\\'), Label = d.VolumeLabel, TotalGb = d.TotalSize / 1073741824.0, FreeGb = d.AvailableFreeSpace / 1073741824.0 };
                disks.Add(row);
                var st = row.FreePct <= cfg.DiskFreeCritPct ? Health.Critical : row.FreePct <= cfg.DiskFreeWarnPct ? Health.Warning : Health.Ok;
                checks.Add(new CheckResult(cat, $"Laufwerk {row.Name}", st,
                    $"{row.FreeGb:0.#} GB frei von {row.TotalGb:0.#} GB ({row.FreePct:0}% frei)."));
            }
            catch { }
        }

        // Zustand der physischen Datenträger
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk"));
            var bad = new List<string>();
            int n = 0;
            foreach (ManagementObject o in q.Get())
            {
                using (o)
                {
                    n++;
                    int h = Convert.ToInt32(o["HealthStatus"] ?? 0);
                    if (h != 0) bad.Add($"{o["FriendlyName"]} ({(h == 1 ? "Warnung" : "Fehlerhaft")})");
                }
            }
            if (n > 0)
                checks.Add(new CheckResult(cat, "Physische Datenträger", bad.Count == 0 ? Health.Ok : Health.Critical,
                    bad.Count == 0 ? $"{n} Datenträger melden den Zustand „Gesund“." : "Problem: " + string.Join(", ", bad)));
        }
        catch { }

        return (checks, disks);
    }

    static bool Exists(string sub)
    {
        using var k = Registry.LocalMachine.OpenSubKey(sub);
        return k != null;
    }
}
