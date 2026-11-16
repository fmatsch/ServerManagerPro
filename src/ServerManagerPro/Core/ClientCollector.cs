using System.Management;

namespace ServerManagerPro.Core;

/// <summary>Wer ist gerade am Server? RDP/Konsole-Sitzungen, SMB-Clients und offene TCP-Verbindungen.</summary>
public static class ClientCollector
{
    public static (List<SessionRow> rows, List<CheckResult> checks, int rdpActive, int rdpTotal, int smb, int tcp) Collect(
        List<Native.TcpConn> tcp)
    {
        const string cat = "Clients";
        var rows = new List<SessionRow>();
        var checks = new List<CheckResult>();
        int rdpActive = 0, rdpTotal = 0, smb = 0;

        try
        {
            foreach (var s in Native.Sessions())
            {
                bool rdp = s.Station.StartsWith("RDP", StringComparison.OrdinalIgnoreCase);
                rows.Add(new SessionRow
                {
                    Kind = rdp ? "Remotedesktop" : "Konsole",
                    User = s.User,
                    Client = s.Client,
                    State = s.State,
                    Info = $"Sitzung {s.Id}",
                });
                if (rdp) { rdpTotal++; if (s.Active) rdpActive++; }
            }
        }
        catch { }

        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\SMB");
            scope.Connect();
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_SmbSession"));
            foreach (ManagementObject o in q.Get())
            {
                using (o)
                {
                    smb++;
                    rows.Add(new SessionRow
                    {
                        Kind = "Dateifreigabe (SMB)",
                        User = o["ClientUserName"]?.ToString() ?? "",
                        Client = o["ClientComputerName"]?.ToString() ?? "",
                        State = "Verbunden",
                        Info = $"{o["NumOpens"] ?? 0} offene Dateien · {Age(o["SecondsExists"])}",
                    });
                }
            }
        }
        catch { /* ohne Adminrechte nicht lesbar */ }

        int established = tcp.Count(t => t.State == 5);
        checks.Add(new CheckResult(cat, "Angemeldete Benutzer",
            Health.Ok,
            rows.Count(r => r.Kind != "Dateifreigabe (SMB)") == 0
                ? "Niemand ist per Konsole oder Remotedesktop angemeldet."
                : $"{rdpActive} aktive und {rdpTotal - rdpActive} getrennte/inaktive Remotedesktop-Sitzung(en)."));
        if (rdpTotal - rdpActive >= 3)
            checks.Add(new CheckResult(cat, "Getrennte Sitzungen", Health.Warning,
                $"{rdpTotal - rdpActive} getrennte Remotedesktop-Sitzungen belegen Ressourcen und Lizenzen."));
        checks.Add(new CheckResult(cat, "Dateifreigaben-Clients", Health.Ok,
            smb == 0 ? "Keine SMB-Clients verbunden (oder nur mit Administratorrechten lesbar)." : $"{smb} SMB-Sitzung(en) aktiv."));
        return (rows, checks, rdpActive, rdpTotal, smb, established);
    }

    static string Age(object? seconds)
    {
        try { return BackupCollector.Age(TimeSpan.FromSeconds(Convert.ToDouble(seconds))); } catch { return ""; }
    }
}
