using System.Collections.Concurrent;
using MySqlConnector;
using Npgsql;

namespace ServerManagerPro.Core;

/// <summary>
/// Optionale Detailwerte für PostgreSQL und MySQL/MariaDB über eine Verbindung mit einem schreibgeschützten Benutzer.
/// Ohne hinterlegte Verbindung zeigen diese Datenbanken nur Zustand, CPU, RAM und Verbindungen.
/// </summary>
public static class DbExtra
{
    sealed record Cached(DateTime At, List<KeyValuePair<string, string>> Metrics, List<SqlDbRow> Dbs, Health Status);
    static readonly ConcurrentDictionary<string, Cached> Cache = new();
    static readonly ConcurrentDictionary<string, (DateTime at, double count)> Prev = new();

    /// <summary>Eine Zeile der Einstellung: "engine|Teil des Dienstnamens (optional)|Verbindungszeichenfolge".</summary>
    static string? FindConnection(string engine, string serviceName, AppSettings cfg)
    {
        foreach (var line in cfg.DbConnections)
        {
            var p = line.Split('|', 3);
            if (p.Length < 3) continue;
            string e = p[0].Trim().ToLowerInvariant();
            bool pg = e is "postgres" or "postgresql" or "pg", my = e is "mysql" or "mariadb";
            if (engine == "PostgreSQL" && !pg) continue;
            if (engine is "MySQL" or "MariaDB" && !my) continue;
            if (p[1].Trim().Length > 0 && !serviceName.Contains(p[1].Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            return p[2].Trim();
        }
        return null;
    }

    public static void Enrich(DatabaseInfo db, string serviceName, AppSettings cfg)
    {
        var cs = FindConnection(db.Engine, serviceName, cfg);
        if (cs == null) return;
        string key = serviceName;
        if (Cache.TryGetValue(key, out var c) && (DateTime.Now - c.At).TotalSeconds < 30)
        {
            Apply(db, c);
            return;
        }
        var metrics = new List<KeyValuePair<string, string>>();
        var dbs = new List<SqlDbRow>();
        var st = Health.Ok;
        try
        {
            if (db.Engine == "PostgreSQL") Postgres(cs, key, cfg, metrics, dbs, ref st);
            else Mysql(cs, key, cfg, metrics, dbs, ref st);
        }
        catch (Exception ex)
        {
            metrics.Clear(); dbs.Clear();
            string msg = ex.GetBaseException().Message;
            if (msg.Length > 140) msg = msg[..140] + " …";
            metrics.Add(new("Detailabfrage", "fehlgeschlagen: " + msg));
            st = Health.Warning;
        }
        c = new Cached(DateTime.Now, metrics, dbs, st);
        Cache[key] = c;
        Apply(db, c);
    }

    static void Apply(DatabaseInfo db, Cached c)
    {
        db.Metrics.AddRange(c.Metrics);
        db.Databases.AddRange(c.Dbs);
        db.Status = HealthExt.Worst(db.Status, c.Status);
    }

    static double Rate(string key, double count)
    {
        var now = DateTime.UtcNow;
        double rate = 0;
        if (Prev.TryGetValue(key, out var p) && (now - p.at).TotalSeconds > 1 && count >= p.count) rate = (count - p.count) / (now - p.at).TotalSeconds;
        Prev[key] = (now, count);
        return rate;
    }

    // ---------- PostgreSQL ----------
    static void Postgres(string cs, string key, AppSettings cfg, List<KeyValuePair<string, string>> m, List<SqlDbRow> dbs, ref Health st)
    {
        var b = new NpgsqlConnectionStringBuilder(cs) { Timeout = 5, CommandTimeout = 8, Pooling = false };
        using var conn = new NpgsqlConnection(b.ConnectionString);
        conn.Open();
        long active = 0, idle = 0, waiting = 0, total = 0, max = 0; double longest = 0;
        using (var cmd = new NpgsqlCommand(@"SELECT count(*) FILTER (WHERE state='active'), count(*) FILTER (WHERE state LIKE 'idle%'),
                count(*) FILTER (WHERE wait_event_type='Lock'),
                COALESCE(max(EXTRACT(EPOCH FROM now()-query_start)) FILTER (WHERE state='active'),0), count(*)
                FROM pg_stat_activity WHERE backend_type='client backend'", conn))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { active = r.GetInt64(0); idle = r.GetInt64(1); waiting = r.GetInt64(2); longest = Convert.ToDouble(r.GetValue(3)); total = r.GetInt64(4); }
        using (var cmd = new NpgsqlCommand("SHOW max_connections", conn)) max = Convert.ToInt64(cmd.ExecuteScalar());

        double xacts = 0, deadlocks = 0;
        using (var cmd = new NpgsqlCommand(@"SELECT d.datname, pg_database_size(d.datname), COALESCE(s.xact_commit+s.xact_rollback,0), COALESCE(s.deadlocks,0)
                FROM pg_database d LEFT JOIN pg_stat_database s ON s.datid=d.oid WHERE NOT d.datistemplate AND d.datallowconn ORDER BY 2 DESC", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                double x = Convert.ToDouble(r.GetValue(2)); xacts += x; deadlocks += Convert.ToDouble(r.GetValue(3));
                dbs.Add(new SqlDbRow { Name = r.GetString(0), DataMb = Convert.ToDouble(r.GetValue(1)) / 1048576.0, TransactionsPerSec = Rate(key + "|" + r.GetString(0), x) });
            }
        double tps = Rate(key + "|all", xacts);
        m.Add(new("Verbindungen", $"{total} von {max} erlaubt ({active} aktiv, {idle} im Leerlauf)"));
        m.Add(new("Wartend auf Sperren", waiting.ToString()));
        m.Add(new("Längste aktive Abfrage", $"{longest:0} s"));
        m.Add(new("Transaktionen/s", $"{tps:0.0}"));
        m.Add(new("Deadlocks (seit Statistikreset)", $"{deadlocks:0}"));
        double use = max > 0 ? total * 100.0 / max : 0;
        if (use >= 90) st = Health.Critical; else if (use >= 80) st = Health.Warning;
        if (waiting > 3 || longest > 600) st = HealthExt.Worst(st, Health.Warning);
        if (use >= 80) m.Insert(0, new("Auffälligkeiten", $"{use:0} % der erlaubten Verbindungen belegt"));
        if (longest > 600) m.Insert(0, new("Auffälligkeiten", $"Abfrage läuft seit {longest:0} s"));
    }

    // ---------- MySQL / MariaDB ----------
    static void Mysql(string cs, string key, AppSettings cfg, List<KeyValuePair<string, string>> m, List<SqlDbRow> dbs, ref Health st)
    {
        var b = new MySqlConnectionStringBuilder(cs) { ConnectionTimeout = 5, DefaultCommandTimeout = 8, Pooling = false };
        using var conn = new MySqlConnection(b.ConnectionString);
        conn.Open();
        var status = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new MySqlCommand("SHOW GLOBAL STATUS WHERE Variable_name IN ('Threads_connected','Threads_running','Slow_queries','Innodb_row_lock_current_waits','Questions','Aborted_connects')", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) status[r.GetString(0)] = double.TryParse(r.GetString(1), out var d) ? d : 0;
        double max = 0;
        using (var cmd = new MySqlCommand("SHOW VARIABLES LIKE 'max_connections'", conn))
        using (var r = cmd.ExecuteReader()) if (r.Read()) max = double.TryParse(r.GetString(1), out var d) ? d : 0;
        double longest = 0;
        try
        {
            using var cmd = new MySqlCommand("SELECT COALESCE(MAX(time),0) FROM information_schema.processlist WHERE command <> 'Sleep' AND user <> 'system user'", conn);
            longest = Convert.ToDouble(cmd.ExecuteScalar());
        }
        catch { }
        using (var cmd = new MySqlCommand("SELECT table_schema, COALESCE(SUM(data_length+index_length),0) FROM information_schema.tables GROUP BY table_schema ORDER BY 2 DESC", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) dbs.Add(new SqlDbRow { Name = r.GetString(0), DataMb = Convert.ToDouble(r.GetValue(1)) / 1048576.0 });

        double conns = status.GetValueOrDefault("Threads_connected"), running = status.GetValueOrDefault("Threads_running"), waits = status.GetValueOrDefault("Innodb_row_lock_current_waits");
        m.Add(new("Verbindungen", $"{conns:0} von {max:0} erlaubt ({running:0} aktiv)"));
        m.Add(new("Abfragen/s", $"{Rate(key + "|q", status.GetValueOrDefault("Questions")):0.0}"));
        m.Add(new("Wartend auf Zeilensperren", $"{waits:0}"));
        m.Add(new("Längste laufende Abfrage", $"{longest:0} s"));
        m.Add(new("Langsame Abfragen (gesamt)", $"{status.GetValueOrDefault("Slow_queries"):0}"));
        double use = max > 0 ? conns * 100.0 / max : 0;
        if (use >= 90) st = Health.Critical; else if (use >= 80) st = Health.Warning;
        if (waits > 3 || longest > 600) st = HealthExt.Worst(st, Health.Warning);
        if (use >= 80) m.Insert(0, new("Auffälligkeiten", $"{use:0} % der erlaubten Verbindungen belegt"));
        if (longest > 600) m.Insert(0, new("Auffälligkeiten", $"Abfrage läuft seit {longest:0} s"));
    }
}
