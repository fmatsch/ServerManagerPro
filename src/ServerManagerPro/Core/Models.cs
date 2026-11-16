using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerManagerPro.Core;

public enum Health { Ok = 0, Info = 1, Warning = 2, Critical = 3, Unknown = 4 }

public static class HealthExt
{
    public static Health Worst(Health a, Health b)
    {
        static int Rank(Health h) => h switch { Health.Critical => 3, Health.Warning => 2, Health.Ok => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    public static string Label(this Health h) => h switch
    {
        Health.Ok => "In Ordnung",
        Health.Warning => "Warnung",
        Health.Critical => "Kritisch",
        Health.Info => "Hinweis",
        _ => "Unbekannt"
    };
}

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed record CheckResult(string Category, string Name, Health Status, string Detail);

public sealed class CategoryGroup
{
    public string Title { get; init; } = "";
    public Health Status { get; init; }
    public List<CheckResult> Items { get; init; } = new();
}

public class ProcessRow : Observable
{
    int _pid; string _name = ""; double _cpu; double _memMb; int _threads; int _session; string _user = "";
    public int Pid { get => _pid; set => Set(ref _pid, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public double Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public double MemMb { get => _memMb; set => Set(ref _memMb, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public int Session { get => _session; set => Set(ref _session, value); }
    public string User { get => _user; set => Set(ref _user, value); }
}

public class ServiceRow : Observable
{
    string _name = "", _display = "", _state = "", _start = "", _account = "", _info = "";
    int _pid;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string DisplayName { get => _display; set => Set(ref _display, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public string StartMode { get => _start; set => Set(ref _start, value); }
    public string Account { get => _account; set => Set(ref _account, value); }
    public int Pid { get => _pid; set => Set(ref _pid, value); }
    public string Info { get => _info; set => Set(ref _info, value); }
    public uint ExitCode { get; set; }
    public bool Delayed { get; set; }
    public bool IsRunning => State == "Running";
}

public sealed class DatabaseInfo
{
    public string Engine { get; set; } = "";
    public string Instance { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string State { get; set; } = "";
    public int Pid { get; set; }
    public double Cpu { get; set; }
    public double MemMb { get; set; }
    public int Connections { get; set; }
    public int ClientHosts { get; set; }
    public string Ports { get; set; } = "";
    public Health Status { get; set; }
    public string Summary { get; set; } = "";
    public List<KeyValuePair<string, string>> Metrics { get; } = new();
    public List<SqlDbRow> Databases { get; } = new();
}

public sealed class SqlDbRow
{
    public string Name { get; set; } = "";
    public double DataMb { get; set; }
    public double LogMb { get; set; }
    public double ActiveTransactions { get; set; }
    public double TransactionsPerSec { get; set; }
    public double BackupThroughput { get; set; }
}

public sealed class BackupItem
{
    public string Product { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Running { get; set; }
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public Health Status { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class TaskRow
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string State { get; set; } = "";
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public string LastResult { get; set; } = "";
    public bool LastOk { get; set; }
}

public sealed class SessionRow
{
    public string Kind { get; set; } = "";
    public string User { get; set; } = "";
    public string Client { get; set; } = "";
    public string State { get; set; } = "";
    public string Info { get; set; } = "";
}

public sealed class EventRow
{
    public DateTime Time { get; set; }
    public string Log { get; set; } = "";
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public int Id { get; set; }
    public string Message { get; set; } = "";
}

public sealed class DiskRow
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedPct => TotalGb <= 0 ? 0 : (TotalGb - FreeGb) / TotalGb * 100;
    public double FreePct => TotalGb <= 0 ? 0 : FreeGb / TotalGb * 100;
}

public sealed class MetricsSnapshot
{
    public double Cpu, MemUsedPct, MemUsedGb, MemTotalGb, DiskBusy, DiskReadBps, DiskWriteBps, NetRxBps, NetTxBps;
    public int Processes, Threads;
    public double[] Cores = Array.Empty<double>();
}

public sealed class Snapshot
{
    public DateTime Time { get; set; } = DateTime.Now;
    public List<CheckResult> Checks { get; set; } = new();
    public List<DatabaseInfo> Databases { get; set; } = new();
    public List<BackupItem> Backups { get; set; } = new();
    public List<TaskRow> Tasks { get; set; } = new();
    public List<SessionRow> Sessions { get; set; } = new();
    public List<EventRow> Events { get; set; } = new();
    public List<DiskRow> Disks { get; set; } = new();
    public int RdpActive { get; set; }
    public int RdpTotal { get; set; }
    public int SmbSessions { get; set; }
    public int TcpEstablished { get; set; }
    public bool BackupRunning { get; set; }
    public bool IsAdmin { get; set; }
}
