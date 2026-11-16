using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace ServerManagerPro.Core;

/// <summary>Erkennt laufende und vergangene Sicherungen (Windows Server Backup, Veeam, Acronis …, geplante Aufgaben).</summary>
public static class BackupCollector
{
    const string Cat = "Sicherung";

    static readonly (string product, string svcRegex, string[] runProcs)[] Products =
    {
        ("Veeam", "^Veeam", new[] { "VeeamAgent", "Veeam.Backup.Manager", "Veeam.Agent.Configurator" }),
        ("Acronis", "^(Acronis|AcrSch|mms$)", new[] { "TrueImage", "backup_worker", "ABRCore" }),
        ("Backup Exec", "^BackupExec", new[] { "bengine", "beremote" }),
        ("Macrium Reflect", "^Macrium", new[] { "ReflectBin", "MacriumBackupMessage" }),
        ("Altaro / Hornetsecurity VM Backup", "^(Altaro|Hornet.*Backup)", Array.Empty<string>()),
        ("NAKIVO", "^Nakivo", Array.Empty<string>()),
        ("Datto / StorageCraft", "^(Datto|ShadowProtect|StorageCraft)", Array.Empty<string>()),
        ("Cohesity / Commvault", "^(Cohesity|GxCVD|Commvault)", Array.Empty<string>()),
    };

    public static (List<BackupItem> items, List<TaskRow> tasks, List<CheckResult> checks, bool running) Collect(
        List<ServiceRow> services, List<ProcRecord> procs, bool sqlBackupRunning, AppSettings cfg)
    {
        var items = new List<BackupItem>();
        var checks = new List<CheckResult>();
        bool running = sqlBackupRunning;
        var procNames = procs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---- Windows Server Backup / Windows-Sicherung ----
        try
        {
            var wb = services.FirstOrDefault(s => s.Name.Equals("wbengine", StringComparison.OrdinalIgnoreCase));
            bool wbRunning = (wb?.IsRunning ?? false) || procNames.Contains("wbadmin");
            var (ok, fail) = ReadBackupLog();
            if (ok != null || fail != null || wbRunning)
            {
                var it = new BackupItem
                {
                    Product = "Windows Server Backup",
                    Source = "Ereignisprotokoll Microsoft-Windows-Backup",
                    Running = wbRunning,
                    LastSuccess = ok,
                    LastFailure = fail,
                };
                it.Status = fail != null && (ok == null || fail > ok) ? Health.Warning : Health.Ok;
                it.Detail = it.Running ? "Sicherung oder Wiederherstellung läuft gerade."
                    : it.Status == Health.Warning ? "Letzter Lauf ist fehlgeschlagen." : "Letzter Lauf erfolgreich.";
                items.Add(it);
                running |= wbRunning;
            }
        }
        catch { }

        // ---- Produkte anderer Hersteller ----
        foreach (var (product, rx, runProcs) in Products)
        {
            var svcs = services.Where(s => Regex.IsMatch(s.Name, rx, RegexOptions.IgnoreCase)).ToList();
            if (svcs.Count == 0) continue;
            bool job = runProcs.Any(p => procNames.Contains(p));
            var stopped = svcs.Where(s => s.StartMode == "Auto" && !s.IsRunning).ToList();
            var it = new BackupItem
            {
                Product = product,
                Source = $"{svcs.Count} Dienst(e)",
                Running = job,
                Status = stopped.Count > 0 ? Health.Warning : Health.Ok,
                Detail = stopped.Count > 0
                    ? $"Dienst gestoppt: {string.Join(", ", stopped.Take(3).Select(s => s.DisplayName))}"
                    : job ? "Auftrag läuft gerade." : "Dienste laufen, kein Auftrag aktiv.",
            };
            items.Add(it);
            running |= job;
        }

        // ---- eigene Tools: WinServBackup ----
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinServBackup");
            if (Directory.Exists(dir) && !items.Any(i => i.Product == "WinServBackup"))
            {
                bool job = procNames.Any(n => n.StartsWith("WinServBackup", StringComparison.OrdinalIgnoreCase));
                items.Add(new BackupItem { Product = "WinServBackup", Source = dir, Running = job, Status = Health.Ok, Detail = job ? "Sicherung läuft." : "Installiert." });
                running |= job;
            }
        }
        catch { }

        // ---- VSS ----
        var vss = services.FirstOrDefault(s => s.Name.Equals("VSS", StringComparison.OrdinalIgnoreCase));
        bool vssActive = vss?.IsRunning ?? false;
        if (vssActive)
            items.Add(new BackupItem { Product = "Schattenkopien (VSS)", Source = "Dienst VSS", Running = true, Status = Health.Info, Detail = "Der Schattenkopie-Dienst ist aktiv – ein Snapshot wird erstellt oder genutzt." });
        running |= vssActive && items.Count(i => i.Running && i.Product != "Schattenkopien (VSS)") > 0;

        // ---- geplante Aufgaben ----
        var tasks = new List<TaskRow>();
        try { tasks = ReadTasks(cfg); } catch { }
        foreach (var t in tasks.Where(t => t.State == "Läuft")) running = true;
        if (sqlBackupRunning)
            items.Add(new BackupItem { Product = "Datenbank-Sicherung", Source = "SQL Server Leistungsindikator", Running = true, Status = Health.Info, Detail = "Eine Datenbank wird gerade gesichert oder wiederhergestellt." });

        // ---- Ergebnis-Checks ----
        var running_names = items.Where(i => i.Running && i.Product != "Schattenkopien (VSS)").Select(i => i.Product)
            .Concat(tasks.Where(t => t.State == "Läuft").Select(t => t.Name)).Distinct().ToList();
        checks.Add(new CheckResult(Cat, "Sicherung aktiv",
            running_names.Count > 0 ? Health.Info : Health.Ok,
            running_names.Count > 0 ? "Läuft gerade: " + string.Join(", ", running_names) : "Aktuell läuft keine Sicherung."));

        var successes = new List<DateTime>();
        successes.AddRange(items.Where(i => i.LastSuccess.HasValue).Select(i => i.LastSuccess!.Value));
        successes.AddRange(tasks.Where(t => t.LastOk && t.LastRun.HasValue).Select(t => t.LastRun!.Value));
        if (successes.Count > 0)
        {
            var newest = successes.Max();
            var age = DateTime.Now - newest;
            var max = TimeSpan.FromHours(Math.Max(1, cfg.BackupMaxAgeHours));
            var st = age <= max ? Health.Ok : age <= max * 3 ? Health.Warning : Health.Critical;
            checks.Add(new CheckResult(Cat, "Letzte erfolgreiche Sicherung", st,
                $"{newest:g} (vor {Age(age)}); erlaubt sind {cfg.BackupMaxAgeHours} Stunden."));
        }
        else if (items.Count(i => i.Product != "Schattenkopien (VSS)" && i.Product != "Datenbank-Sicherung") == 0 && tasks.Count == 0)
        {
            checks.Add(new CheckResult(Cat, "Backup-Lösung", Health.Warning,
                "Keine Sicherung erkannt (Windows Server Backup, Veeam, Acronis, Aufgabenplanung …). Fehlt die Sicherung wirklich?"));
        }
        else
        {
            checks.Add(new CheckResult(Cat, "Letzte erfolgreiche Sicherung", Health.Info,
                "Sicherungssoftware erkannt, aber ein Erfolgszeitpunkt ist nicht auslesbar."));
        }

        foreach (var t in tasks.Where(t => !t.LastOk && t.State != "Läuft" && t.State != "Deaktiviert" && t.LastRun.HasValue && t.LastResult != "Noch nie ausgeführt"))
            checks.Add(new CheckResult(Cat, $"Aufgabe: {t.Name}", Health.Warning, $"Letzter Lauf {t.LastRun:g} endete mit {t.LastResult}."));
        foreach (var it in items.Where(i => i.Status == Health.Warning))
            checks.Add(new CheckResult(Cat, it.Product, Health.Warning, it.Detail));

        return (items, tasks, checks, running);
    }

    public static string Age(TimeSpan a) =>
        a.TotalMinutes < 90 ? $"{a.TotalMinutes:0} Min." : a.TotalHours < 48 ? $"{a.TotalHours:0.#} Std." : $"{a.TotalDays:0.#} Tagen";

    static (DateTime? ok, DateTime? fail) ReadBackupLog()
    {
        DateTime? ok = null, fail = null;
        var q = new EventLogQuery("Microsoft-Windows-Backup", PathType.LogName, "*[System[(EventID=4 or Level=1 or Level=2)]]") { ReverseDirection = true };
        using var r = new EventLogReader(q);
        for (int i = 0; i < 60 && (ok == null || fail == null); i++)
        {
            using var e = r.ReadEvent();
            if (e == null) break;
            if (e.Id == 4) ok ??= e.TimeCreated;
            else if (e.Level is 1 or 2) fail ??= e.TimeCreated;
        }
        return (ok, fail);
    }

    static List<TaskRow> ReadTasks(AppSettings cfg)
    {
        var words = new List<string> { "backup", "sicherung", "veeam", "acronis", "wbadmin", "macrium", "winservbackup", "datensicherung" };
        words.AddRange(cfg.ExtraBackupKeywords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()));
        var res = new List<TaskRow>();
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return res;
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        void Walk(dynamic folder)
        {
            try
            {
                foreach (dynamic t in folder.GetTasks(1))
                {
                    string name = t.Name, path = t.Path;
                    string low = path.ToLowerInvariant();
                    if (path.StartsWith("\\Microsoft\\Windows\\", StringComparison.OrdinalIgnoreCase)
                        && !path.StartsWith("\\Microsoft\\Windows\\Backup", StringComparison.OrdinalIgnoreCase)
                        && !path.StartsWith("\\Microsoft\\Windows\\WindowsBackup", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!words.Any(w => low.Contains(w))) continue;
                    int state = t.State; bool enabled = t.Enabled;
                    int result = t.LastTaskResult;
                    DateTime last = t.LastRunTime, next = t.NextRunTime;
                    res.Add(new TaskRow
                    {
                        Name = name,
                        Path = path,
                        State = !enabled ? "Deaktiviert" : state == 4 ? "Läuft" : state == 2 ? "Wartet" : "Bereit",
                        LastRun = last.Year > 1990 ? last : null,
                        NextRun = next.Year > 1990 && enabled ? next : null,
                        LastOk = result == 0,
                        LastResult = result == 0 ? "Erfolgreich" : result == 267009 ? "Läuft" : result == 267011 ? "Noch nie ausgeführt" : $"0x{result:X8}",
                    });
                }
            }
            catch { }
            try { foreach (dynamic sub in folder.GetFolders(0)) Walk(sub); } catch { }
        }
        Walk(svc.GetFolder("\\"));
        return res.OrderBy(t => t.Name).ToList();
    }
}
