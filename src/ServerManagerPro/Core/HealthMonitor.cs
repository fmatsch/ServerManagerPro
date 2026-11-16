namespace ServerManagerPro.Core;

/// <summary>Koordiniert alle Messungen: schnelle Schleife (Leistung, Prozesse) und Gesundheitsschleife (Dienste, Datenbanken, Backup …).</summary>
public sealed class HealthMonitor : IDisposable
{
    public AppSettings Settings { get; }
    public event Action<MetricsSnapshot>? MetricsUpdated;
    public event Action<List<ProcRecord>>? ProcessesUpdated;
    public event Action<List<ServiceRow>>? ServicesUpdated;
    public event Action<Snapshot>? HealthUpdated;

    readonly MetricsSampler _sampler = new();
    readonly ProcessCollector _procs = new();
    readonly DatabaseCollector _dbs = new();
    readonly DhcpCollector _dhcp = new();
    (AdInfo info, List<CheckResult> checks)? _ad;
    DateTime _adAt = DateTime.MinValue;
    public bool Demo { get; set; }
    readonly Queue<MetricsSnapshot> _recent = new();
    readonly CancellationTokenSource _cts = new();
    List<ProcRecord> _lastProcs = new();
    (List<EventRow> rows, List<CheckResult> checks) _events = (new(), new());
    (List<CheckResult> checks, List<DiskRow> disks) _system = (new(), new());
    DateTime _eventsAt = DateTime.MinValue, _systemAt = DateTime.MinValue;
    volatile bool _forceSlow;
    readonly bool _admin = Native.IsAdmin();

    public HealthMonitor(AppSettings settings) { Settings = settings; }

    public void Start()
    {
        _ = Task.Run(() => Loop(() => FastOnce(), () => Math.Max(1, Settings.RefreshSeconds) * 1000));
        _ = Task.Run(() => Loop(() => HealthOnce(), () => 8000));
    }

    public void RefreshNow() => _forceSlow = true;
    public void ScanDhcpNow() => _dhcp.RequestScan();

    async Task Loop(Action body, Func<int> delay)
    {
        while (!_cts.IsCancellationRequested)
        {
            try { body(); } catch { }
            try { await Task.Delay(delay(), _cts.Token); } catch (TaskCanceledException) { return; }
        }
    }

    public MetricsSnapshot FastOnce()
    {
        var m = _sampler.Sample();
        var p = _procs.Collect();
        lock (_recent) { _recent.Enqueue(m); while (_recent.Count > 15) _recent.Dequeue(); }
        _lastProcs = p;
        MetricsUpdated?.Invoke(m);
        ProcessesUpdated?.Invoke(p);
        return m;
    }

    public Snapshot HealthOnce()
    {
        var snap = new Snapshot { Time = DateTime.Now, IsAdmin = _admin };
        var procs = _lastProcs;
        List<ServiceRow> services = new();
        List<Native.TcpConn> tcp = new();

        T Guard<T>(string area, Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception ex)
            {
                snap.Checks.Add(new CheckResult(area, "Messung fehlgeschlagen", Health.Unknown, ex.GetBaseException().Message));
                return fallback;
            }
        }

        // Leistung (Mittelwert der letzten Messungen, damit kurze Spitzen nicht sofort alarmieren)
        MetricsSnapshot[] recent;
        lock (_recent) recent = _recent.ToArray();
        if (recent.Length > 0)
        {
            double cpu = recent.Average(r => r.Cpu), mem = recent.Average(r => r.MemUsedPct);
            var last = recent[^1];
            snap.Checks.Add(new CheckResult("Leistung", "Prozessor",
                cpu >= Settings.CpuCrit ? Health.Critical : cpu >= Settings.CpuWarn ? Health.Warning : Health.Ok,
                $"{cpu:0}% Auslastung (Mittel der letzten {recent.Length * Math.Max(1, Settings.RefreshSeconds)} s), {last.Processes} Prozesse."));
            snap.Checks.Add(new CheckResult("Leistung", "Arbeitsspeicher",
                mem >= Settings.MemCrit ? Health.Critical : mem >= Settings.MemWarn ? Health.Warning : Health.Ok,
                $"{last.MemUsedGb:0.0} GB von {last.MemTotalGb:0.0} GB belegt ({mem:0}%)."));
            snap.Checks.Add(new CheckResult("Leistung", "Datenträger & Netzwerk", Health.Ok,
                $"Datenträger {last.DiskBusy:0}% ausgelastet, {Fmt.Bytes(last.DiskReadBps + last.DiskWriteBps)}/s · Netzwerk ↓ {Fmt.Bytes(last.NetRxBps)}/s ↑ {Fmt.Bytes(last.NetTxBps)}/s"));
        }

        services = Guard("Dienste", ServiceCollector.Collect, new List<ServiceRow>());
        ServicesUpdated?.Invoke(services);
        tcp = Guard("Clients", Native.TcpTable, new List<Native.TcpConn>());

        snap.Checks.AddRange(Guard("Dienste", () => ServiceCollector.Check(services, Settings), new List<CheckResult>()));

        var db = Guard("Datenbanken", () => _dbs.Collect(services, procs, tcp, Settings), (new List<DatabaseInfo>(), new List<CheckResult>(), false));
        snap.Databases = db.Item1;
        snap.Checks.AddRange(db.Item2);

        var bk = Guard("Sicherung", () => BackupCollector.Collect(services, procs, db.Item3, Settings),
            (new List<BackupItem>(), new List<TaskRow>(), new List<CheckResult>(), false));
        snap.Backups = bk.Item1; snap.Tasks = bk.Item2; snap.BackupRunning = bk.Item4;
        snap.Checks.AddRange(bk.Item3);

        var cl = Guard("Clients", () => ClientCollector.Collect(tcp), (new List<SessionRow>(), new List<CheckResult>(), 0, 0, 0, 0));
        snap.Sessions = cl.Item1; snap.Checks.AddRange(cl.Item2);
        snap.RdpActive = cl.Item3; snap.RdpTotal = cl.Item4; snap.SmbSessions = cl.Item5; snap.TcpEstablished = cl.Item6;

        var net = Guard("Clients", () => NetworkCollector.Collect(tcp, procs), (new List<ConnRow>(), new List<CheckResult>()));
        snap.Connections = net.Item1; snap.Checks.AddRange(net.Item2);

        // Domänencontroller
        if (AdCollector.IsDomainController(services))
        {
            if (_ad == null || _forceSlow || (DateTime.Now - _adAt).TotalSeconds > 120)
            {
                _ad = Guard("Active Directory", () => AdCollector.Collect(services, Settings), ((AdInfo info, List<CheckResult> checks)?)null);
                _adAt = DateTime.Now;
            }
            if (_ad != null) { snap.Ad = _ad.Value.info; snap.Checks.AddRange(_ad.Value.checks); }
        }
        else _ad = null;

        // DHCP-Server
        var dsvc = services.FirstOrDefault(s => s.Name.Equals("DHCPServer", StringComparison.OrdinalIgnoreCase));
        if (dsvc != null)
        {
            var d = Guard("DHCP", () => _dhcp.Collect(dsvc.IsRunning), ((DhcpInfo info, List<CheckResult> checks)?)null);
            if (d != null) { snap.Dhcp = d.Value.info; snap.Checks.AddRange(d.Value.checks); }
        }

        if (Demo) DemoData.Apply(snap);

        if (_forceSlow || (DateTime.Now - _systemAt).TotalSeconds > 30)
        {
            _system = Guard("System", () => SystemChecker.Collect(Settings), (new List<CheckResult>(), new List<DiskRow>()));
            _systemAt = DateTime.Now;
        }
        snap.Checks.AddRange(_system.checks); snap.Disks = _system.disks;

        if (_forceSlow || (DateTime.Now - _eventsAt).TotalSeconds > 90)
        {
            _events = Guard("Ereignisse", () => EventCollector.Collect(Settings), (new List<EventRow>(), new List<CheckResult>()));
            _eventsAt = DateTime.Now;
        }
        snap.Events = _events.rows; snap.Checks.AddRange(_events.checks);
        _forceSlow = false;

        HealthUpdated?.Invoke(snap);
        return snap;
    }

    public static Health Overall(IEnumerable<CheckResult> checks)
    {
        var h = Health.Ok;
        foreach (var c in checks) if (c.Status is Health.Warning or Health.Critical) h = HealthExt.Worst(h, c.Status);
        return h;
    }

    /// <summary>Gesundheitswert 0–100: Kritisch −25, Warnung −8, Unbekannt −2.</summary>
    public static int Score(IEnumerable<CheckResult> checks)
    {
        int s = 100;
        foreach (var c in checks)
            s -= c.Status switch { Health.Critical => 25, Health.Warning => 8, Health.Unknown => 2, _ => 0 };
        return Math.Clamp(s, 0, 100);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sampler.Dispose();
    }
}

public static class Fmt
{
    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}
