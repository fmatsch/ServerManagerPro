namespace ServerManagerPro.Core;

/// <summary>Beispieldaten für Screenshots (Start mit --demo). Werden nie bei normalem Betrieb verwendet.</summary>
static class DemoData
{
    public static void Apply(Snapshot s)
    {
        var ad = new AdInfo
        {
            Demo = true, Domain = "firma.example", Forest = "firma.example", DcName = "DC01.firma.example", Site = "Standort-Wien",
            Level = "Domäne Windows2016Domain, Gesamtstruktur Windows2016Forest",
            Users = 184, UsersEnabled = 171, UsersDisabled = 13, UsersLocked = 1, PasswordNeverExpires = 6, Inactive90 = 9,
            Computers = 96, Groups = 142, DomainAdmins = 4, SysvolOk = true, NetlogonOk = true, Status = Health.Warning,
        };
        ad.Fsmo.Add(new("PDC-Emulator", "DC01.firma.example")); ad.Fsmo.Add(new("RID-Master", "DC01.firma.example"));
        ad.Fsmo.Add(new("Infrastruktur-Master", "DC01.firma.example")); ad.Fsmo.Add(new("Schema-Master", "DC01.firma.example"));
        ad.Fsmo.Add(new("Domänennamen-Master", "DC01.firma.example"));
        ad.Replication.Add(new ReplRow { Partner = "DC02.firma.example", Partition = "DC=firma,DC=example", LastSuccess = DateTime.Now.AddMinutes(-4), Result = "Erfolgreich", Ok = true });
        ad.Replication.Add(new ReplRow { Partner = "DC02.firma.example", Partition = "CN=Configuration,DC=firma,DC=example", LastSuccess = DateTime.Now.AddMinutes(-9), Result = "Erfolgreich", Ok = true });
        foreach (var (n, d) in new[] { ("NTDS", "Active Directory-Domänendienste"), ("Kdc", "Kerberos-Schlüsselverteilungscenter"), ("Netlogon", "Netlogon"), ("DNS", "DNS-Server"), ("DFSR", "DFS-Replikation"), ("ADWS", "Active Directory-Webdienste"), ("W32Time", "Windows-Zeitgeber") })
            ad.Services.Add(new ServiceRow { Name = n, DisplayName = d, State = "Running", StartMode = "Auto" });
        s.Ad = ad;
        s.Checks.Add(new("Active Directory", "Domänendienst (NTDS)", Health.Ok, "Active Directory-Domänendienste laufen."));
        s.Checks.Add(new("Active Directory", "SYSVOL / NETLOGON", Health.Ok, "Beide Freigaben sind erreichbar."));
        s.Checks.Add(new("Active Directory", "Replikation", Health.Ok, "2 Verbindungen, alle erfolgreich."));
        s.Checks.Add(new("Active Directory", "Benutzerkonten", Health.Ok, "171 aktiv, 13 deaktiviert, 96 Computerkonten, 142 Gruppen."));
        s.Checks.Add(new("Active Directory", "Gesperrte Konten", Health.Warning, "1 Konto/Konten sind gerade gesperrt."));

        var d2 = new DhcpInfo { Demo = true, ServiceRunning = true, ScanTime = DateTime.Now.AddMinutes(-2) };
        var rnd = new Random(7);
        void Scope(string id, string name, uint start, uint end, int excl, int leased, int reserved, int conflicts, int declined)
        {
            var row = new DhcpScopeRow { ScopeId = id, Name = name, Range = $"{DhcpCollector.Str(start)} – {DhcpCollector.Str(end)}", State = "Active", Scanned = true };
            uint n = end - start + 1; int idx = 0;
            for (uint ip = start; ip <= end; ip++, idx++)
            {
                var r = new IpRow { Ip = DhcpCollector.Str(ip), Sort = ip, Scope = id };
                if (idx < excl) { r.Status = "Ausgeschlossen"; r.Detail = "Vom Vergeben ausgenommen"; row.Excluded++; }
                else
                {
                    row.Total++;
                    int k = idx - excl;
                    if (k < reserved) { r.Status = "Reserviert"; r.Host = $"drucker-{k + 1}"; r.Mac = $"00-1B-A9-{rnd.Next(256):X2}-{rnd.Next(256):X2}-{rnd.Next(256):X2}"; r.Detail = "Reservierung, Gerät antwortet"; r.Alive = true; row.Reserved++; }
                    else if (k < reserved + leased) { r.Status = "Vergeben"; r.Host = $"PC-{k:000}"; r.Mac = $"3C-52-82-{rnd.Next(256):X2}-{rnd.Next(256):X2}-{rnd.Next(256):X2}"; r.Alive = rnd.Next(10) > 2; r.Detail = (r.Alive ? "Gerät antwortet" : "Gerät offline") + ", Lease bis " + DateTime.Now.AddDays(3).ToString("yyyy-MM-dd HH:mm"); row.Leased++; }
                    else if (k < reserved + leased + conflicts) { r.Status = "Konflikt"; r.Alive = true; r.Host = "scanner-og2.firma.example"; r.Mac = "00-80-77-1A-2B-3C"; r.Detail = "Antwortet, steht aber nicht im DHCP – statische IP oder fremdes Gerät"; row.Conflicts++; }
                    else if (k < reserved + leased + conflicts + declined) { r.Status = "Abgelehnt"; r.Detail = "Ein Client hat die Adresse wegen Konflikt abgelehnt"; row.Declined++; }
                    else { r.Status = "Frei"; r.Detail = "Laut DHCP frei und im Scan ohne Antwort"; }
                }
                d2.Addresses.Add(r);
            }
            row.ServerFree = row.Total - row.Leased - row.Reserved - row.Declined;
            row.ReallyFree = row.ServerFree - row.Conflicts;
            d2.Scopes.Add(row);
        }
        Scope("192.168.10.0", "Büro Wien", U("192.168.10.1"), U("192.168.10.254"), 20, 112, 8, 3, 1);
        Scope("192.168.20.0", "WLAN Gäste", U("192.168.20.1"), U("192.168.20.126"), 0, 107, 0, 0, 0);
        s.Dhcp = d2;
        s.Checks.Add(new("DHCP", "DHCP-Server", Health.Ok, "Dienst läuft."));
        s.Checks.Add(new("DHCP", "Bereich 192.168.10.0", Health.Ok, "Büro Wien: 110 von 234 Adressen wirklich frei (47%)."));
        s.Checks.Add(new("DHCP", "Konflikte 192.168.10.0", Health.Warning, "3 Adresse(n) antworten, obwohl DHCP sie als frei führt (statische IP oder fremder DHCP-Server?)."));
        s.Checks.Add(new("DHCP", "Bereich 192.168.20.0", Health.Warning, "WLAN Gäste: 19 von 126 Adressen wirklich frei (15%)."));
    }

    static uint U(string ip) => DhcpCollector.U(ip);
}
