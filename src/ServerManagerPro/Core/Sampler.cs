using System.Diagnostics;

namespace ServerManagerPro.Core;

/// <summary>Liest CPU, RAM, Datenträger und Netzwerk (Leistungsindikatoren, funktioniert auch ohne Admin-Rechte).</summary>
public sealed class MetricsSampler : IDisposable
{
    PerformanceCounter? _cpu, _diskBusy, _diskRead, _diskWrite, _procs, _threads;
    PerformanceCounter[] _cores = Array.Empty<PerformanceCounter>();
    List<PerformanceCounter> _rx = new(), _tx = new();
    DateTime _netRefresh = DateTime.MinValue;

    public MetricsSampler()
    {
        _cpu = Make("Processor", "% Processor Time", "_Total");
        _diskBusy = Make("PhysicalDisk", "% Disk Time", "_Total");
        _diskRead = Make("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWrite = Make("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        _procs = Make("System", "Processes", "");
        _threads = Make("System", "Threads", "");
        try
        {
            var cat = new PerformanceCounterCategory("Processor");
            _cores = cat.GetInstanceNames()
                .Where(n => n != "_Total" && int.TryParse(n, out _))
                .OrderBy(int.Parse)
                .Select(n => Make("Processor", "% Processor Time", n)!)
                .Where(c => c != null).ToArray();
        }
        catch { }
    }

    static PerformanceCounter? Make(string cat, string counter, string inst)
    {
        try
        {
            var c = string.IsNullOrEmpty(inst) ? new PerformanceCounter(cat, counter) : new PerformanceCounter(cat, counter, inst);
            c.NextValue();
            return c;
        }
        catch { return null; }
    }

    static double Read(PerformanceCounter? c)
    {
        try { return c == null ? 0 : c.NextValue(); } catch { return 0; }
    }

    void RefreshNetwork()
    {
        if ((DateTime.UtcNow - _netRefresh).TotalSeconds < 60) return;
        _netRefresh = DateTime.UtcNow;
        foreach (var c in _rx.Concat(_tx)) c.Dispose();
        _rx = new(); _tx = new();
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (var n in cat.GetInstanceNames())
            {
                var lower = n.ToLowerInvariant();
                if (lower.Contains("loopback") || lower.Contains("isatap") || lower.Contains("teredo")) continue;
                var r = Make("Network Interface", "Bytes Received/sec", n);
                var t = Make("Network Interface", "Bytes Sent/sec", n);
                if (r != null) _rx.Add(r);
                if (t != null) _tx.Add(t);
            }
        }
        catch { }
    }

    public MetricsSnapshot Sample()
    {
        RefreshNetwork();
        var (pct, used, total) = Native.Memory();
        return new MetricsSnapshot
        {
            Cpu = Math.Clamp(Read(_cpu), 0, 100),
            MemUsedPct = pct,
            MemUsedGb = used,
            MemTotalGb = total,
            DiskBusy = Math.Clamp(Read(_diskBusy), 0, 100),
            DiskReadBps = Read(_diskRead),
            DiskWriteBps = Read(_diskWrite),
            NetRxBps = _rx.Sum(Read),
            NetTxBps = _tx.Sum(Read),
            Processes = (int)Read(_procs),
            Threads = (int)Read(_threads),
            Cores = _cores.Select(c => Math.Clamp(Read(c), 0, 100)).ToArray(),
        };
    }

    public void Dispose()
    {
        foreach (var c in new[] { _cpu, _diskBusy, _diskRead, _diskWrite, _procs, _threads }) c?.Dispose();
        foreach (var c in _cores.Concat(_rx).Concat(_tx)) c.Dispose();
    }
}

public sealed record ProcRecord(int Pid, string Name, double Cpu, double MemMb, int Threads, int Session);

/// <summary>Prozessliste inkl. CPU-Auslastung je Prozess (Differenz der Prozessorzeit).</summary>
public sealed class ProcessCollector
{
    readonly Dictionary<int, (TimeSpan cpu, DateTime at)> _prev = new();
    readonly int _cores = Environment.ProcessorCount;

    public List<ProcRecord> Collect()
    {
        var now = DateTime.UtcNow;
        var list = new List<ProcRecord>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid = p.Id;
                seen.Add(pid);
                double cpu = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    if (_prev.TryGetValue(pid, out var old))
                    {
                        double dt = (now - old.at).TotalSeconds;
                        if (dt > 0.2) cpu = Math.Clamp((t - old.cpu).TotalSeconds / dt / _cores * 100, 0, 100);
                        else cpu = double.NaN; // zu kurzer Abstand: letzten Wert behalten
                    }
                    if (double.IsNaN(cpu)) cpu = 0; else _prev[pid] = (t, now);
                }
                catch { }
                string name = pid == 0 ? "Leerlaufprozess" : p.ProcessName;
                list.Add(new ProcRecord(pid, name, cpu, p.WorkingSet64 / 1048576.0, SafeThreads(p), SafeSession(p)));
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);
        return list;
    }

    static int SafeThreads(Process p) { try { return p.Threads.Count; } catch { return 0; } }
    static int SafeSession(Process p) { try { return p.SessionId; } catch { return 0; } }
}
