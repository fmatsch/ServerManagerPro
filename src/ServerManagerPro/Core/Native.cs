using System.Net;
using System.Runtime.InteropServices;

namespace ServerManagerPro.Core;

public static class Native
{
    // ---- Speicher ----
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    static long _pIdle, _pKernel, _pUser;

    /// <summary>Gesamt-CPU wie im Task-Manager, praktisch ohne Kosten (Differenz der Systemzeiten).</summary>
    public static double CpuTotal()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long di = idle - _pIdle, dk = kernel - _pKernel, du = user - _pUser;
        bool first = _pKernel == 0;
        _pIdle = idle; _pKernel = kernel; _pUser = user;
        long total = dk + du; // Kernelzeit enthält die Leerlaufzeit
        return first || total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }

    public static (double usedPct, double usedGb, double totalGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (total <= 0 ? 0 : used / total * 100, used, total);
    }

    // ---- Terminaldienste / RDP ----
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr h, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr h, int session, int cls, out IntPtr buf, out int bytes);

    [StructLayout(LayoutKind.Sequential)]
    struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    static string Query(int session, int cls)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, cls, out var p, out _)) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; } finally { WTSFreeMemory(p); }
    }

    static readonly string[] WtsStates =
        { "Aktiv", "Verbunden", "Verbindungsaufbau", "Schatten", "Getrennt", "Leerlauf", "Lauscht", "Zurückgesetzt", "Down", "Init" };

    public sealed record WtsSession(int Id, string Station, string State, bool Active, string User, string Client);

    public static List<WtsSession> Sessions()
    {
        var list = new List<WtsSession>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buf, out int count)) return list;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(buf + i * size);
                string user = Query(si.SessionId, 5);
                string dom = Query(si.SessionId, 7);
                string client = Query(si.SessionId, 10);
                string station = Marshal.PtrToStringUni(si.pWinStationName) ?? "";
                if (string.IsNullOrEmpty(user)) continue; // Listener / Dienste-Sitzung
                string state = si.State >= 0 && si.State < WtsStates.Length ? WtsStates[si.State] : si.State.ToString();
                list.Add(new WtsSession(si.SessionId, station, state, si.State == 0,
                    string.IsNullOrEmpty(dom) ? user : dom + "\\" + user, client));
            }
        }
        finally { WTSFreeMemory(buf); }
        return list;
    }

    // ---- TCP-Tabelle mit PID ----
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int cls, int reserved);

    public sealed record TcpConn(int Pid, int State, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort);

    public static List<TcpConn> TcpTable()
    {
        var res = new List<TcpConn>();
        Read(2, 24, res);   // AF_INET: MIB_TCPROW_OWNER_PID = 24 Bytes
        Read(23, 56, res);  // AF_INET6: MIB_TCP6ROW_OWNER_PID = 56 Bytes
        return res;
    }

    static void Read(int af, int rowSize, List<TcpConn> res)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, 5, 0) != 0) continue;
                int n = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                for (int i = 0; i < n; i++, row += rowSize)
                {
                    if (af == 2)
                    {
                        int st = Marshal.ReadInt32(row, 0);
                        var la = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                        int lp = Port(Marshal.ReadInt32(row, 8));
                        var ra = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                        int rp = Port(Marshal.ReadInt32(row, 16));
                        int pid = Marshal.ReadInt32(row, 20);
                        res.Add(new TcpConn(pid, st, la, lp, ra, rp));
                    }
                    else
                    {
                        var lb = new byte[16]; Marshal.Copy(row, lb, 0, 16);
                        int lp = Port(Marshal.ReadInt32(row, 20));
                        var rb = new byte[16]; Marshal.Copy(row + 24, rb, 0, 16);
                        int rp = Port(Marshal.ReadInt32(row, 44));
                        int st = Marshal.ReadInt32(row, 48);
                        int pid = Marshal.ReadInt32(row, 52);
                        res.Add(new TcpConn(pid, st, new IPAddress(lb), lp, new IPAddress(rb), rp));
                    }
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    public static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException) return true;
            if (e is System.Management.ManagementException me && me.ErrorCode == System.Management.ManagementStatus.AccessDenied) return true;
            if (e.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
