using System.Net;
using System.Net.Sockets;

namespace ServerManagerPro.Core;

/// <summary>Offene TCP-Verbindungen, getrennt nach lokalem Netz (LAN) und Internet.</summary>
public static class NetworkCollector
{
    public const string ScopeLan = "Lokal (LAN)";
    public const string ScopeInternet = "Internet";
    public const string ScopeLoop = "Dieser Rechner (Loopback)";
    public const string ScopeListen = "Lauschend";

    static readonly Dictionary<int, string> States = new()
    {
        [2] = "Lauschend", [3] = "SYN gesendet", [4] = "SYN empfangen", [5] = "Verbunden", [6] = "FIN-Wait 1",
        [7] = "FIN-Wait 2", [8] = "Close-Wait", [9] = "Last-ACK", [10] = "Closing", [11] = "Time-Wait",
    };

    /// <summary>Gehört die Adresse zum eigenen/privaten Netz (nicht Internet)?</summary>
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)            // Link-Local
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT / Tailscale
                || b[0] >= 224;                            // Multicast/Reserviert
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC; // fc00::/7
        return true;
    }

    static bool Unspecified(IPAddress ip) => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);

    static string Fmt(IPAddress ip, int port) =>
        (ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString()) + ":" + port;

    public static (List<ConnRow> rows, List<CheckResult> checks) Collect(List<Native.TcpConn> tcp, List<ProcRecord> procs)
    {
        var names = procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        var rows = new List<ConnRow>();
        foreach (var t in tcp)
        {
            if (t.State == 11 || t.State == 12) continue; // TIME_WAIT/DELETE_TCB sind nur Rauschen
            string scope;
            if (t.State == 2) scope = ScopeListen;
            else if (Unspecified(t.Remote)) continue;
            else scope = IPAddress.IsLoopback(t.Remote) ? ScopeLoop : IsLocal(t.Remote) ? ScopeLan : ScopeInternet;
            rows.Add(new ConnRow
            {
                Scope = scope,
                Pid = t.Pid,
                Process = names.TryGetValue(t.Pid, out var n) ? n : t.Pid == 4 ? "System" : "?",
                Local = Fmt(t.Local, t.LocalPort),
                Remote = t.State == 2 ? "–" : Fmt(t.Remote, t.RemotePort),
                State = States.TryGetValue(t.State, out var s) ? s : t.State.ToString(),
            });
        }
        rows = rows.OrderBy(r => r.Scope == ScopeLan ? 0 : r.Scope == ScopeInternet ? 1 : r.Scope == ScopeLoop ? 2 : 3)
                   .ThenBy(r => r.Process, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Remote).ToList();

        int loop = rows.Count(r => r.Scope == ScopeLoop), lan = rows.Count(r => r.Scope == ScopeLan), inet = rows.Count(r => r.Scope == ScopeInternet), listen = rows.Count(r => r.Scope == ScopeListen);
        var hosts = rows.Where(r => r.Scope == ScopeInternet).Select(r => r.Remote[..r.Remote.LastIndexOf(':')]).Distinct().Count();
        var checks = new List<CheckResult>
        {
            new("Clients", "Netzwerkverbindungen", (lan + inet) > 20000 ? Health.Warning : Health.Ok,
                $"{lan} im lokalen Netz, {inet} ins Internet ({hosts} Gegenstellen), {loop} innerhalb dieses Rechners, {listen} lauschende Ports."),
        };
        return (rows, checks);
    }
}
