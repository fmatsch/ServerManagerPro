using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ServerManagerPro.Core;

/// <summary>Erkennt Datenbankserver über ihre Dienste und liest Auslastung, Verbindungen und (bei SQL Server) Leistungsindikatoren.</summary>
public sealed class DatabaseCollector
{
    readonly Dictionary<string, PerformanceCounter?> _counters = new();

    static (string engine, string instance)? Match(ServiceRow s, AppSettings cfg)
    {
        string n = s.Name;
        if (n.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)) return ("SQL Server", "Standardinstanz");
        if (n.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase)) return ("SQL Server", n[6..]);
        if (n.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase)) return ("PostgreSQL", s.DisplayName);
        if (n.StartsWith("MariaDB", StringComparison.OrdinalIgnoreCase)) return ("MariaDB", n);
        if (n.StartsWith("MySQL", StringComparison.OrdinalIgnoreCase)) return ("MySQL", n);
        if (n.StartsWith("OracleService", StringComparison.OrdinalIgnoreCase)) return ("Oracle", n[13..]);
        if (n.StartsWith("MongoDB", StringComparison.OrdinalIgnoreCase)) return ("MongoDB", n);
        if (n.StartsWith("FirebirdServer", StringComparison.OrdinalIgnoreCase) || n.StartsWith("FirebirdGuardian", StringComparison.OrdinalIgnoreCase)) return ("Firebird", n);
        if (n.Equals("Redis", StringComparison.OrdinalIgnoreCase)) return ("Redis", n);
        if (n.StartsWith("MSSQLServerOLAP", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var pat in cfg.ExtraDatabaseServices.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var rx = "^" + Regex.Escape(pat.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(n, rx, RegexOptions.IgnoreCase) || Regex.IsMatch(s.DisplayName, rx, RegexOptions.IgnoreCase))
                return ("Datenbank", s.DisplayName);
        }
        return null;
    }

    PerformanceCounter? Counter(string cat, string name, string inst)
    {
        string key = $"{cat}|{name}|{inst}";
        if (_counters.TryGetValue(key, out var c)) return c;
        try
        {
            c = string.IsNullOrEmpty(inst) ? new PerformanceCounter(cat, name) : new PerformanceCounter(cat, name, inst);
            c.NextValue();
        }
        catch { c = null; }
        _counters[key] = c;
        return c;
    }

    double? Val(string cat, string name, string inst = "")
    {
        try { return Counter(cat, name, inst)?.NextValue(); } catch { return null; }
    }

    public (List<DatabaseInfo> dbs, List<CheckResult> checks, bool backupRunning) Collect(
        List<ServiceRow> services, List<ProcRecord> procs, List<Native.TcpConn> tcp, AppSettings cfg)
    {
        var dbs = new List<DatabaseInfo>();
        var checks = new List<CheckResult>();
        bool backupRunning = false;
        const string cat = "Datenbanken";

        foreach (var s in services)
        {
            var m = Match(s, cfg);
            if (m == null) continue;
            var (engine, inst) = m.Value;
            var db = new DatabaseInfo { Engine = engine, Instance = inst, ServiceName = s.Name, State = s.State, Pid = s.Pid };

            if (s.IsRunning && s.Pid > 0)
            {
                var self = procs.FirstOrDefault(p => p.Pid == s.Pid);
                var same = self == null ? new List<ProcRecord>() : procs.Where(p => p.Name == self.Name).ToList();
                db.Cpu = same.Sum(p => p.Cpu);
                db.MemMb = same.Sum(p => p.MemMb);

                var pids = same.Select(p => p.Pid).ToHashSet();
                var listen = tcp.Where(t => t.State == 2 && pids.Contains(t.Pid)).Select(t => t.LocalPort).ToHashSet();
                var conns = tcp.Where(t => t.State == 5 && pids.Contains(t.Pid) && listen.Contains(t.LocalPort)).ToList();
                db.Connections = conns.Count;
                db.ClientHosts = conns.Select(c => c.Remote.ToString()).Where(a => a != "127.0.0.1" && a != "::1").Distinct().Count();
                db.Ports = string.Join(", ", listen.OrderBy(x => x).Take(4));

                if (engine == "SQL Server") CollectSql(db, s.Name, cfg, ref backupRunning);
                else
                {
                    db.Status = db.Cpu >= cfg.CpuWarn ? Health.Warning : Health.Ok;
                    DbExtra.Enrich(db, s.Name, cfg);
                    foreach (var d in db.Databases) if (d.BackupThroughput > 0) backupRunning = true;
                }
            }
            else
            {
                db.Status = s.StartMode == "Auto" ? Health.Critical : Health.Info;
            }
            Summarize(db, cfg);
            dbs.Add(db);

            checks.Add(new CheckResult(cat, $"{engine} – {inst}", db.Status, db.Summary));
        }

        // SQL Server Agent
        foreach (var a in services.Where(x => x.Name.Equals("SQLSERVERAGENT", StringComparison.OrdinalIgnoreCase)
                                           || x.Name.StartsWith("SQLAgent$", StringComparison.OrdinalIgnoreCase)))
        {
            if (!a.IsRunning && a.StartMode == "Auto")
                checks.Add(new CheckResult(cat, a.DisplayName, Health.Warning, "SQL Server Agent ist gestoppt – geplante Jobs (auch Backups) laufen nicht."));
            else if (a.IsRunning)
                checks.Add(new CheckResult(cat, a.DisplayName, Health.Ok, "SQL Server Agent läuft."));
        }

        if (dbs.Count == 0)
            checks.Add(new CheckResult(cat, "Datenbankserver", Health.Info, "Keine Datenbankdienste gefunden (SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB …)."));
        return (dbs, checks, backupRunning);
    }

    void CollectSql(DatabaseInfo db, string svc, AppSettings cfg, ref bool backupRunning)
    {
        string prefix = svc.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "SQLServer" : "MSSQL$" + svc[6..];
        string C(string group) => $"{prefix}:{group}";
        void Add(string label, double? v, string fmt = "0")
        {
            if (v.HasValue) db.Metrics.Add(new(label, v.Value.ToString(fmt)));
        }

        var conns = Val(C("General Statistics"), "User Connections");
        var batch = Val(C("SQL Statistics"), "Batch Requests/sec");
        var hit = Val(C("Buffer Manager"), "Buffer cache hit ratio");
        var ple = Val(C("Buffer Manager"), "Page life expectancy");
        var blocked = Val(C("General Statistics"), "Processes blocked");
        var lockWaits = Val(C("Locks"), "Lock Waits/sec", "_Total");
        var mem = Val(C("Memory Manager"), "Total Server Memory (KB)");
        var memTarget = Val(C("Memory Manager"), "Target Server Memory (KB)");

        Add("Benutzerverbindungen", conns);
        Add("Batch-Abfragen/s", batch, "0.0");
        Add("Pufferzwischenspeicher-Trefferquote %", hit, "0.0");
        Add("Seitenlebensdauer (s)", ple);
        Add("Blockierte Prozesse", blocked);
        Add("Sperrwartezeiten/s", lockWaits, "0.0");
        if (mem.HasValue) db.Metrics.Add(new("Server-Speicher", $"{mem.Value / 1048576.0:0.0} GB" + (memTarget.HasValue ? $" von {memTarget.Value / 1048576.0:0.0} GB" : "")));
        if (conns.HasValue) db.Connections = Math.Max(db.Connections, (int)conns.Value);

        try
        {
            var dbCat = new PerformanceCounterCategory(C("Databases"));
            foreach (var inst in dbCat.GetInstanceNames().Where(n => n != "_Total" && n != "mssqlsystemresource").OrderBy(n => n))
            {
                var row = new SqlDbRow
                {
                    Name = inst,
                    DataMb = (Val(C("Databases"), "Data File(s) Size (KB)", inst) ?? 0) / 1024.0,
                    LogMb = (Val(C("Databases"), "Log File(s) Size (KB)", inst) ?? 0) / 1024.0,
                    ActiveTransactions = Val(C("Databases"), "Active Transactions", inst) ?? 0,
                    TransactionsPerSec = Val(C("Databases"), "Transactions/sec", inst) ?? 0,
                    BackupThroughput = Val(C("Databases"), "Backup/Restore Throughput/sec", inst) ?? 0,
                };
                if (row.BackupThroughput > 0) backupRunning = true;
                db.Databases.Add(row);
            }
        }
        catch { }

        var st = Health.Ok;
        var notes = new List<string>();
        if (db.Cpu >= cfg.CpuCrit) { st = Health.Critical; notes.Add($"CPU {db.Cpu:0}%"); }
        else if (db.Cpu >= cfg.CpuWarn) { st = Health.Warning; notes.Add($"CPU {db.Cpu:0}%"); }
        if (blocked is > 0) { st = HealthExt.Worst(st, Health.Warning); notes.Add($"{blocked:0} blockierte Prozesse"); }
        if (ple is > 0 and < 300) { st = HealthExt.Worst(st, Health.Warning); notes.Add($"Seitenlebensdauer nur {ple:0}s"); }
        if (hit is > 0 and < 90) { st = HealthExt.Worst(st, Health.Warning); notes.Add($"Trefferquote {hit:0}%"); }
        if (db.Databases.Any(d => d.BackupThroughput > 0)) notes.Add("Datenbank-Sicherung läuft");
        db.Status = st;
        if (notes.Count > 0) db.Metrics.Insert(0, new("Auffälligkeiten", string.Join("; ", notes)));
        if (conns == null) db.Metrics.Add(new("Hinweis", "SQL-Leistungsindikatoren nicht lesbar – als Administrator starten."));
    }

    static void Summarize(DatabaseInfo db, AppSettings cfg)
    {
        if (db.State != "Running")
        {
            db.Summary = $"Dienst {db.ServiceName} ist {db.State}.";
            return;
        }
        var parts = new List<string> { "läuft" };
        parts.Add($"{db.Connections} Verbindung(en)");
        if (db.ClientHosts > 0) parts.Add($"{db.ClientHosts} Client-Rechner");
        parts.Add($"CPU {db.Cpu:0.#}%");
        parts.Add($"RAM {db.MemMb / 1024.0:0.0} GB");
        var attn = db.Metrics.FirstOrDefault(m => m.Key == "Auffälligkeiten");
        if (attn.Key != null) parts.Add(attn.Value);
        db.Summary = string.Join(" · ", parts);
    }
}
