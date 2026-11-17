namespace ServerManagerPro.Core;

/// <summary>
/// Schreibt aus den laufenden Messungen den Verlauf: pro Minute ein Mittelwert (mit Höchstwert) für diesen Rechner und für
/// entfernte Server sowie jede Zustandsänderung einer Prüfung („In Ordnung → Kritisch“). Liest nur, was ohnehin gemessen wird.
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    readonly AppSettings _cfg;
    readonly object _lock = new();
    readonly Timer _purge;
    static readonly string LocalSource = "local";

    // Minutenmittel für diesen Rechner
    int _n; DateTime _start = DateTime.MinValue;
    double _cpuS, _cpuM, _memS, _memM, _diskS, _diskM, _rxS, _txS;
    double _score = double.NaN, _conn = double.NaN, _rdp = double.NaN;
    int _overall;

    readonly Dictionary<string, Dictionary<string, string>> _prev = new();   // Quelle → Prüfung → Stufe
    readonly Dictionary<string, DateTime> _remoteLast = new();

    public HistoryRecorder(HealthMonitor mon, RemoteMonitor remotes, AppSettings cfg)
    {
        _cfg = cfg;
        mon.MetricsUpdated += OnMetrics;
        mon.HealthUpdated += OnHealth;
        remotes.Updated += OnRemote;
        _purge = new Timer(_ => { if (_cfg.HistoryEnabled) HistoryStore.Purge(_cfg.HistoryDays); }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(24));
    }

    static string Level(Health h) => h == Health.Critical ? "Kritisch" : h == Health.Warning ? "Warnung" : "In Ordnung";

    void OnMetrics(MetricsSnapshot m)
    {
        if (!_cfg.HistoryEnabled) return;
        lock (_lock)
        {
            var now = DateTime.Now;
            if (_start == DateTime.MinValue) _start = now;
            _n++;
            _cpuS += m.Cpu; _cpuM = Math.Max(_cpuM, m.Cpu);
            _memS += m.MemUsedPct; _memM = Math.Max(_memM, m.MemUsedPct);
            _diskS += m.DiskBusy; _diskM = Math.Max(_diskM, m.DiskBusy);
            _rxS += m.NetRxBps; _txS += m.NetTxBps;
            if ((now - _start).TotalSeconds >= 60) Flush(now);
        }
    }

    void Flush(DateTime now)
    {
        if (_n > 0)
        {
            var mid = _start.AddSeconds((now - _start).TotalSeconds / 2);
            HistoryStore.Append(LocalSource, new Sample(mid, _cpuS / _n, _cpuM, _memS / _n, _memM, _diskS / _n, _diskM, _rxS / _n, _txS / _n, _score, _overall, _conn, _rdp));
        }
        _n = 0; _start = DateTime.MinValue;
        _cpuS = _cpuM = _memS = _memM = _diskS = _diskM = _rxS = _txS = 0;
    }

    void OnHealth(Snapshot s)
    {
        if (!_cfg.HistoryEnabled) return;
        lock (_lock)
        {
            _score = HealthMonitor.Score(s.Checks);
            var o = HealthMonitor.Overall(s.Checks);
            _overall = o == Health.Critical ? 2 : o == Health.Warning ? 1 : 0;
            _conn = s.Databases.Count > 0 ? s.Databases.Sum(d => d.Connections) : double.NaN;
            _rdp = s.RdpTotal > 0 || s.Sessions.Count > 0 ? s.RdpActive : double.NaN;
        }
        Transitions(LocalSource, s.Checks, s.Time);
    }

    void OnRemote(List<RemoteState> list)
    {
        if (!_cfg.HistoryEnabled) return;
        foreach (var st in list)
        {
            var checks = new List<CheckResult>
            {
                new("Erreichbarkeit", "Server nicht erreichbar", st.Failures >= 2 ? Health.Critical : Health.Ok, st.Ok ? "Erreichbar." : st.Error),
            };
            if (st.Ok && st.Payload != null)
            {
                checks.AddRange(st.Payload.Checks.Select(c => new CheckResult(c.Category, c.Name, Enum.TryParse<Health>(c.Status, out var h) ? h : Health.Unknown, c.Detail)));
                lock (_lock)
                {
                    if (!_remoteLast.TryGetValue(st.Name, out var last) || (DateTime.Now - last).TotalSeconds >= 55)
                    {
                        _remoteLast[st.Name] = DateTime.Now;
                        var p = st.Payload;
                        HistoryStore.Append(st.Name, new Sample(DateTime.Now, p.Cpu, p.Cpu, p.MemUsedPct, p.MemUsedPct, double.NaN, double.NaN, double.NaN, double.NaN,
                            p.Score, p.Overall == "Critical" ? 2 : p.Overall == "Warning" ? 1 : 0, double.NaN, double.NaN));
                    }
                }
            }
            Transitions(st.Name, checks, DateTime.Now);
        }
    }

    /// <summary>Nur für Tests: wertet Prüfungen einer Quelle aus.</summary>
    public void RecordChecks(string source, List<CheckResult> checks, DateTime at) => Transitions(source, checks, at);

    /// <summary>Vergleicht die Stufen mit dem letzten Stand und schreibt jede Änderung.</summary>
    void Transitions(string source, List<CheckResult> checks, DateTime at)
    {
        var cur = new Dictionary<string, string>();
        var detail = new Dictionary<string, CheckResult>();
        foreach (var c in checks) { string k = c.Category + "\u0001" + c.Name; cur[k] = Level(c.Status); detail[k] = c; }

        List<ChangeRec> changes = new();
        lock (_lock)
        {
            bool first = !_prev.TryGetValue(source, out var prev);
            prev ??= new();
            foreach (var (k, lvl) in cur)
            {
                string old = prev.TryGetValue(k, out var o) ? o : (first ? "Beim Start" : "In Ordnung");
                bool changed = first ? lvl != "In Ordnung" : old != lvl;
                if (!changed) continue;
                var c = detail[k];
                changes.Add(new ChangeRec { Time = at, Source = source, From = old, To = lvl, Category = c.Category, Name = c.Name, Detail = c.Detail });
            }
            _prev[source] = cur;
        }
        foreach (var c in changes.Take(60)) HistoryStore.AppendChange(c);
    }

    public void Dispose()
    {
        _purge.Dispose();
        lock (_lock) Flush(DateTime.Now);
    }
}
