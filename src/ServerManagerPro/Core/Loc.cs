using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServerManagerPro.Core;

/// <summary>
/// Zweisprachige Oberfläche (Deutsch/Englisch). Die Quelltexte sind deutsch; Englisch kommt aus einer Tabelle.
/// Texte mit {0}, {1} … sind Vorlagen. Was nicht in der Tabelle steht, bleibt unverändert (deutsch).
/// </summary>
public static class Loc
{
    public static bool English { get; private set; }

    public static void Init(string setting)
    {
        English = setting == "en" || (setting is not "de" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de");
        if (English && !_built) Build();
    }

    static readonly Dictionary<string, string> Exact = new();
    static readonly List<(Regex rx, string en)> Templates = new();
    static bool _built;

    static void Build()
    {
        _built = true;
        foreach (var (de, en) in Table)
        {
            if (!de.Contains('{')) { Exact[de] = en; continue; }
            var rx = "^" + Regex.Escape(de) + "$";
            rx = Regex.Replace(rx, @"\\\{(\d)\}", "(?<g$1>.+?)");
            Templates.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.Compiled), en));
        }
    }

    public static string T(string? de)
    {
        if (!English || string.IsNullOrEmpty(de)) return de ?? "";
        if (Exact.TryGetValue(de, out var en)) return en;
        foreach (var (rx, tpl) in Templates)
        {
            var m = rx.Match(de);
            if (!m.Success) continue;
            return Regex.Replace(tpl, @"\{(\d)\}", x => m.Groups["g" + x.Groups[1].Value].Success ? Translate(m.Groups["g" + x.Groups[1].Value].Value) : x.Value);
        }
        // Listen wie "a, b, c" und "x: y" stückweise übersetzen
        if (de.Contains(": ") && de.Length < 400)
        {
            int i = de.IndexOf(": ", StringComparison.Ordinal);
            var head = de[..i];
            if (Exact.TryGetValue(head, out var eh)) return eh + ": " + de[(i + 2)..];
        }
        return de;
    }

    /// <summary>Wie T, aber für eingesetzte Teilwerte (Zahlen bleiben unberührt).</summary>
    static string Translate(string part) => Exact.TryGetValue(part, out var en) ? en : part;

    /// <summary>Übersetzt die Anzeigetexte eines Messergebnisses (nur wenn Englisch aktiv ist).</summary>
    public static void Localize(Snapshot s)
    {
        if (!English) return;
        s.Checks = s.Checks.Select(c => new CheckResult(T(c.Category), T(c.Name), c.Status, T(c.Detail))).ToList();
        foreach (var sec in s.Sections)
        {
            sec.Group = T(sec.Group); sec.Title = T(sec.Title); sec.Note = T(sec.Note);
            for (int i = 0; i < sec.Headers.Length; i++) sec.Headers[i] = T(sec.Headers[i]);
            foreach (var row in sec.Rows) for (int i = 0; i < row.Length; i++) row[i] = T(row[i]);
        }
        foreach (var d in s.Databases)
        {
            d.Summary = T(d.Summary);
            var m = d.Metrics.Select(kv => new KeyValuePair<string, string>(T(kv.Key), T(kv.Value))).ToList();
            d.Metrics.Clear(); d.Metrics.AddRange(m);
        }
        foreach (var b in s.Backups) { b.Product = T(b.Product); b.Source = T(b.Source); b.Detail = T(b.Detail); }
        foreach (var t in s.Tasks) { t.State = T(t.State); t.LastResult = T(t.LastResult); }
        foreach (var r in s.Sessions) { r.Kind = T(r.Kind); r.State = T(r.State); r.Info = T(r.Info); }
        foreach (var e in s.Events) e.Level = T(e.Level);
        if (s.Ad != null)
        {
            s.Ad.Level = T(s.Ad.Level);
            var f = s.Ad.Fsmo.Select(kv => new KeyValuePair<string, string>(T(kv.Key), kv.Value)).ToList();
            s.Ad.Fsmo.Clear(); s.Ad.Fsmo.AddRange(f);
            foreach (var r in s.Ad.Replication) r.Result = T(r.Result);
        }
        if (s.Dhcp != null) foreach (var sc in s.Dhcp.Scopes) sc.Note = T(sc.Note);
    }

    // ---------- Oberfläche (XAML) ----------
    public static void Apply(DependencyObject root)
    {
        if (!English) return;
        Walk(root);
    }

    static void Walk(object? node)
    {
        switch (node)
        {
            case Window w: w.Title = T(w.Title); break;
            case TextBlock tb when tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text): tb.Text = T(tb.Text); break;
            case ContentControl cc:
                if (cc.Content is string s) cc.Content = T(s);
                break;
            case DataGrid dg:
                foreach (var c in dg.Columns) if (c.Header is string h) c.Header = T(h);
                break;
        }
        if (node is FrameworkElement fe)
        {
            if (fe.ToolTip is string tt) fe.ToolTip = T(tt);
            var an = System.Windows.Automation.AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(an) && !an.StartsWith("{")) System.Windows.Automation.AutomationProperties.SetName(fe, T(an));
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                Walk(child);
    }

    // ---------- Tabelle ----------
    static readonly (string de, string en)[] Table =
    {
        // --- Navigation, Kopfzeile ---
        ("Übersicht", "Overview"), ("Leistung", "Performance"), ("Prozesse", "Processes"), ("Dienste", "Services"),
        ("Datenbanken", "Databases"), ("Sicherung", "Backup"), ("Clients", "Clients"), ("Netzwerk", "Network"),
        ("Sicherheit & Rollen", "Security & Roles"), ("Active Directory", "Active Directory"), ("DHCP & IPs", "DHCP & IPs"),
        ("Ereignisse", "Events"), ("Einstellungen", "Settings"),
        ("Als Administrator starten", "Run as administrator"), ("Als Admin", "Run as admin"),
        ("Für Dienste steuern, SMB-Clients und SQL-Leistungsindikatoren sind Administratorrechte nötig.", "Controlling services, SMB clients and SQL performance counters require administrator rights."),
        ("Overlay", "Overlay"), ("Kennzahlen als dauerhaftes Overlay anzeigen (Strg+Alt+O)", "Show key metrics as a permanent overlay (Ctrl+Alt+O)"),
        ("Aktualisieren", "Refresh"), ("Prüfe …", "Checking …"),
        ("Server gesund", "Server healthy"), ("Warnungen", "Warnings"), ("Probleme erkannt", "Problems detected"),
        ("Gesundheit {0}/100 · alles in Ordnung", "Health {0}/100 · all good"), ("Gesundheit {0}/100 · {1} Auffälligkeit(en)", "Health {0}/100 · {1} issue(s)"),
        ("In Ordnung", "OK"), ("Warnung", "Warning"), ("Kritisch", "Critical"), ("Hinweis", "Note"), ("Unbekannt", "Unknown"),
        // --- Übersicht / Leistung ---
        ("Prozessor", "Processor"), ("Arbeitsspeicher", "Memory"), ("Datenträger", "Disk"), ("CPU", "CPU"),
        ("Datenträger-Aktivität", "Disk activity"), ("Logische Prozessoren", "Logical processors"), ("Laufwerke", "Drives"),
        (" GB frei von ", " GB free of "), ("Laufzeit", "Uptime"), ("Remote-Sitzungen", "Remote sessions"),
        ("Dateiserver-Clients", "File server clients"), ("Datenbankverbindungen", "Database connections"), ("Datenbankdienste", "Database services"),
        ("TCP-Verbindungen", "TCP connections"), ("davon ins Internet", "of which to the internet"),
        ("{0} von {1} laufen", "{0} of {1} running"), ("läuft gerade", "running now"), ("läuft nicht", "not running"),
        ("{0} T {1} Std.", "{0} d {1} h"), ("{0} aktiv / {1}", "{0} active / {1}"),
        ("Lokales Netz (LAN)", "Local network (LAN)"), ("Internet", "Internet"), ("Gegenstellen im Internet", "Remote hosts on the internet"),
        ("Innerhalb dieses Rechners", "Inside this computer"), ("Lauschende Ports", "Listening ports"),
        // --- Prozesse / Dienste ---
        ("Nach Name oder PID filtern", "Filter by name or PID"), ("Prozess beenden", "End process"), ("Name", "Name"), ("PID", "PID"),
        ("CPU %", "CPU %"), ("Speicher (MB)", "Memory (MB)"), ("Threads", "Threads"), ("Sitzung", "Session"),
        ("Nach Name oder Anzeigename filtern", "Filter by name or display name"), ("Starten", "Start"), ("Beenden", "Stop"), ("Neu starten", "Restart"),
        ("Anzeigename", "Display name"), ("Status", "Status"), ("Starttyp", "Start type"), ("Konto", "Account"),
        ("{0} von {1} Diensten laufen", "{0} of {1} services running"), ("Bitte warten …", "Please wait …"),
        ("Tipp: Starten Sie die Anwendung als Administrator.", "Tip: run the application as administrator."),
        ("Dienst steuern", "Control service"), ("Beenden nicht möglich", "Cannot end process"),
        // --- Datenbanken ---
        ("Es wurde kein Datenbankserver gefunden. Erkannt werden SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB, Firebird und Redis; weitere Dienste lassen sich unter Einstellungen ergänzen.",
         "No database server found. SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB, Firebird and Redis are detected; more services can be added under Settings."),
        ("Verbindungen", "Connections"), ("Client-Rechner", "Client computers"), ("Speicher MB", "Memory MB"), ("Ports", "Ports"),
        ("Datenbank", "Database"), ("Daten (MB)", "Data (MB)"), ("Log (MB)", "Log (MB)"), ("Aktive Transaktionen", "Active transactions"),
        ("Transaktionen/s", "Transactions/s"), ("Sicherung KB/s", "Backup KB/s"), ("Auffälligkeiten", "Notable"),
        ("Benutzerverbindungen", "User connections"), ("Batch-Abfragen/s", "Batch requests/s"),
        ("Pufferzwischenspeicher-Trefferquote %", "Buffer cache hit ratio %"), ("Seitenlebensdauer (s)", "Page life expectancy (s)"),
        ("Blockierte Prozesse", "Blocked processes"), ("Sperrwartezeiten/s", "Lock waits/s"), ("Server-Speicher", "Server memory"),
        ("Wartend auf Sperren", "Waiting for locks"), ("Wartend auf Zeilensperren", "Waiting for row locks"),
        ("Längste aktive Abfrage", "Longest active query"), ("Längste laufende Abfrage", "Longest running query"),
        ("Langsame Abfragen (gesamt)", "Slow queries (total)"), ("Abfragen/s", "Queries/s"),
        ("Deadlocks (seit Statistikreset)", "Deadlocks (since stats reset)"), ("Detailabfrage", "Detail query"),
        ("{0} von {1} erlaubt ({2} aktiv, {3} im Leerlauf)", "{0} of {1} allowed ({2} active, {3} idle)"), ("{0} von {1} erlaubt ({2} aktiv)", "{0} of {1} allowed ({2} active)"),
        ("Datenbankserver", "Database server"), ("Keine Datenbankdienste gefunden (SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB …).", "No database services found (SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB …)."),
        ("Dienst {0} ist {1}.", "Service {0} is {1}."), ("Standardinstanz", "Default instance"),
        ("läuft", "running"), ("{0} Verbindung(en)", "{0} connection(s)"), ("{0} Client-Rechner", "{0} client computer(s)"),
        ("SQL Server Agent ist gestoppt – geplante Jobs (auch Backups) laufen nicht.", "SQL Server Agent is stopped – scheduled jobs (including backups) are not running."),
        ("SQL Server Agent läuft.", "SQL Server Agent is running."),
        ("SQL-Leistungsindikatoren nicht lesbar – als Administrator starten.", "SQL performance counters not readable – run as administrator."),
        // --- Sicherung ---
        ("Erkannte Sicherungslösungen", "Detected backup solutions"), ("Lösung", "Solution"), ("Quelle", "Source"), ("Läuft", "Running"),
        ("Letzter Erfolg", "Last success"), ("Letzter Fehler", "Last failure"), ("Geplante Sicherungsaufgaben", "Scheduled backup tasks"),
        ("Aufgabe", "Task"), ("Pfad", "Path"), ("Letzter Lauf", "Last run"), ("Ergebnis", "Result"), ("Nächster Lauf", "Next run"),
        ("Eine Sicherung läuft gerade", "A backup is running"), ("Sicherung in Ordnung", "Backup OK"), ("Sicherung prüfen", "Check backup"),
        ("Sicherung aktiv", "Backup active"), ("Aktuell läuft keine Sicherung.", "No backup is running right now."),
        ("Läuft gerade: {0}", "Running now: {0}"), ("Letzte erfolgreiche Sicherung", "Last successful backup"),
        ("{0} (vor {1}); erlaubt sind {2} Stunden.", "{0} ({1} ago); {2} hours allowed."),
        ("Keine Sicherung erkannt (Windows Server Backup, Veeam, Acronis, Aufgabenplanung …). Fehlt die Sicherung wirklich?", "No backup detected (Windows Server Backup, Veeam, Acronis, Task Scheduler …). Is the backup really missing?"),
        ("Backup-Lösung", "Backup solution"),
        ("Sicherungssoftware erkannt, aber ein Erfolgszeitpunkt ist nicht auslesbar.", "Backup software detected, but the time of the last success cannot be read."),
        ("Sicherung oder Wiederherstellung läuft gerade.", "A backup or restore is running right now."), ("Letzter Lauf ist fehlgeschlagen.", "The last run failed."), ("Letzter Lauf erfolgreich.", "The last run succeeded."),
        ("Auftrag läuft gerade.", "A job is running right now."), ("Dienste laufen, kein Auftrag aktiv.", "Services are running, no job active."),
        ("Datenbank-Sicherung", "Database backup"), ("Eine Datenbank wird gerade gesichert oder wiederhergestellt.", "A database is being backed up or restored right now."),
        ("Der Schattenkopie-Dienst ist aktiv – ein Snapshot wird erstellt oder genutzt.", "The shadow copy service is active – a snapshot is being created or used."),
        ("Erfolgreich", "Succeeded"), ("Noch nie ausgeführt", "Never run"), ("Bereit", "Ready"), ("Wartet", "Queued"), ("Deaktiviert", "Disabled"),
        // --- Clients / Netzwerk ---
        ("Art", "Type"), ("Benutzer", "User"), ("Client", "Client"), ("Details", "Details"),
        ("Angemeldete Benutzer", "Signed-in users"), ("Niemand ist per Konsole oder Remotedesktop angemeldet.", "Nobody is signed in via console or Remote Desktop."),
        ("{0} aktive und {1} getrennte/inaktive Remotedesktop-Sitzung(en).", "{0} active and {1} disconnected/idle Remote Desktop session(s)."),
        ("Getrennte Sitzungen", "Disconnected sessions"), ("{0} getrennte Remotedesktop-Sitzungen belegen Ressourcen und Lizenzen.", "{0} disconnected Remote Desktop sessions are using resources and licenses."),
        ("Dateifreigaben-Clients", "File share clients"), ("Keine SMB-Clients verbunden (oder nur mit Administratorrechten lesbar).", "No SMB clients connected (or only readable with administrator rights)."),
        ("{0} SMB-Sitzung(en) aktiv.", "{0} SMB session(s) active."),
        ("Remotedesktop", "Remote Desktop"), ("Konsole", "Console"), ("Dateifreigabe (SMB)", "File share (SMB)"), ("Verbunden", "Connected"), ("Aktiv", "Active"), ("Getrennt", "Disconnected"),
        ("Anzeige:", "View:"), ("Getrennt: lokal (LAN) und Internet", "Separate: local (LAN) and internet"), ("Nur lokales Netz (LAN)", "Local network (LAN) only"),
        ("Nur Internet", "Internet only"), ("Nur dieser Rechner (Loopback)", "This computer only (loopback)"), ("Alle, unsortiert", "All, unsorted"),
        ("Nach Prozess, Adresse oder Port filtern", "Filter by process, address or port"), ("Prozess", "Process"), ("Lokal", "Local"), ("Gegenstelle", "Remote"), ("Netz", "Network"),
        ("Lokal (LAN)", "Local (LAN)"), ("Dieser Rechner (Loopback)", "This computer (loopback)"), ("Lauschend", "Listening"), ("Netzwerkverbindungen", "Network connections"),
        ("{0} im lokalen Netz, {1} ins Internet ({2} Gegenstellen), {3} innerhalb dieses Rechners, {4} lauschende Ports.", "{0} on the local network, {1} to the internet ({2} remote hosts), {3} inside this computer, {4} listening ports."),
        // --- Active Directory ---
        ("Dienst", "Service"), ("Replikation", "Replication"), ("Partner", "Partner"), ("Partition", "Partition"),
        ("Letzte Synchronisierung", "Last synchronization"), ("Fehler in Folge", "Consecutive failures"), ("Betriebsmaster (FSMO)", "Operations masters (FSMO)"),
        ("Rolle", "Role"), ("Inhaber", "Holder"), ("Benutzer gesamt", "Users total"), ("Aktiv", "Active"), ("Deaktiviert", "Disabled"), ("Gesperrt", "Locked out"),
        ("Inaktiv (90 Tage)", "Inactive (90 days)"), ("Kennwort läuft nie ab", "Password never expires"), ("Computer", "Computers"), ("Gruppen", "Groups"),
        ("Domänen-Admins", "Domain admins"), ("SYSVOL / NETLOGON", "SYSVOL / NETLOGON"), ("erreichbar", "reachable"), ("Problem", "Problem"),
        ("Active Directory-Domänendienste laufen.", "Active Directory Domain Services are running."), ("Domänendienst (NTDS)", "Domain service (NTDS)"),
        ("Beide Freigaben sind erreichbar.", "Both shares are reachable."),
        ("SYSVOL oder NETLOGON ist nicht freigegeben – Gruppenrichtlinien und Anmeldeskripte fallen aus.", "SYSVOL or NETLOGON is not shared – group policies and logon scripts will fail."),
        ("Benutzerkonten", "User accounts"), ("{0} aktiv, {1} deaktiviert, {2} Computerkonten, {3} Gruppen.", "{0} enabled, {1} disabled, {2} computer accounts, {3} groups."),
        ("Gesperrte Konten", "Locked-out accounts"), ("{0} Konto/Konten sind gerade gesperrt.", "{0} account(s) are locked out right now."), ("Keine gesperrten Konten.", "No locked-out accounts."),
        ("{0} Mitglied(er)", "{0} member(s)"), ("Inaktive Konten", "Inactive accounts"), ("{0} aktive Konten ohne Anmeldung in 90 Tagen.", "{0} enabled accounts without sign-in for 90 days."),
        ("Kennwörter ohne Ablauf", "Passwords without expiry"), ("{0} aktive Konten haben ein Kennwort, das nie abläuft.", "{0} enabled accounts have a password that never expires."),
        ("{0} von {1} Replikationsverbindungen fehlerhaft.", "{0} of {1} replication links failing."), ("{0} Verbindungen, alle erfolgreich.", "{0} links, all successful."),
        ("Keine Replikationspartner (einziger Domänencontroller).", "No replication partners (single domain controller)."), ("Erfolgreich", "Succeeded"),
        ("PDC-Emulator", "PDC emulator"), ("RID-Master", "RID master"), ("Infrastruktur-Master", "Infrastructure master"), ("Schema-Master", "Schema master"), ("Domänennamen-Master", "Domain naming master"),
        ("(Beispieldaten)", "(sample data)"), ("  (Beispieldaten)", "  (sample data)"),
        // --- DHCP ---
        ("DHCP-Server", "DHCP server"), ("Netz jetzt scannen", "Scan network now"), ("Bereich", "Scope"), ("Adressen", "Addresses"), ("Gesamt", "Total"),
        ("Vergeben", "Leased"), ("Reserviert", "Reserved"), ("DHCP frei", "DHCP free"), ("Konflikte", "Conflicts"), ("Wirklich frei", "Really free"),
        ("Adressen:", "Addresses:"), ("Alle", "All"), ("Wirklich frei (vergebbar)", "Really free (assignable)"), ("Konflikte und abgelehnt", "Conflicts and declined"),
        ("Nach IP, Name oder MAC filtern", "Filter by IP, name or MAC"), ("IP-Adresse", "IP address"), ("MAC", "MAC"),
        ("DHCP-Server ist gestoppt", "DHCP server is stopped"), ("DHCP-Server – {0} Bereich(e)", "DHCP server – {0} scope(s)"), ("DHCP-Daten nicht lesbar: {0}", "DHCP data not readable: {0}"),
        ("Der DHCP-Serverdienst ist gestoppt – Clients erhalten keine Adressen.", "The DHCP server service is stopped – clients will not receive addresses."), ("Dienst läuft.", "Service is running."),
        ("Der DHCP-Server hat keine IPv4-Bereiche.", "The DHCP server has no IPv4 scopes."),
        ("Bereich {0}", "Scope {0}"), ("Konflikte {0}", "Conflicts {0}"), ("Abgelehnte Adressen {0}", "Declined addresses {0}"),
        ("{0}: {1} von {2} Adressen wirklich frei ({3}%)", "{0}: {1} of {2} addresses really free ({3}%)"),
        ("{0} Adresse(n) antworten, obwohl DHCP sie als frei führt (statische IP oder fremder DHCP-Server?).", "{0} address(es) respond although DHCP lists them as free (static IP or rogue DHCP server?)."),
        ("{0} Adresse(n) wurden von Clients wegen Konflikt abgelehnt.", "{0} address(es) were declined by clients because of a conflict."),
        ("Scan wird gestartet …", "Starting scan …"), ("{0} von {1} Adressen geprüft", "{0} of {1} addresses checked"),
        ("Frei", "Free"), ("Konflikt", "Conflict"), ("Abgelehnt", "Declined"), ("Ausgeschlossen", "Excluded"), ("Vergeben", "Leased"), ("Reserviert", "Reserved"), ("Dieser Server", "This server"),
        ("Laut DHCP frei und im Scan ohne Antwort", "Free per DHCP and no response in the scan"), ("Laut DHCP frei (Scan läuft noch)", "Free per DHCP (scan still running)"),
        ("Antwortet, steht aber nicht im DHCP – statische IP oder fremdes Gerät", "Responds but is not in DHCP – static IP or foreign device"),
        ("Ein Client hat die Adresse wegen Konflikt abgelehnt", "A client declined the address because of a conflict"), ("Vom Vergeben ausgenommen", "Excluded from assignment"), ("Eigene Adresse", "Own address"),
        ("Reservierung, Gerät antwortet", "Reservation, device responds"), ("Reservierung, Gerät offline", "Reservation, device offline"), ("Reservierung", "Reservation"),
        ("Gerät antwortet", "device responds"), ("Gerät offline", "device offline"),
        ("DHCP-Failover", "DHCP failover"), ("Beziehung", "Relationship"), ("Modus", "Mode"), ("Zustand", "State"), ("Bereiche", "Scopes"),
        ("DHCP IPv6-Bereiche", "DHCP IPv6 scopes"), ("Präfix", "Prefix"), ("Frei", "Free"), ("Auslastung %", "Utilization %"),
        ("zufällige/lokale Adresse (z. B. Smartphone mit privater MAC, VM, Container)", "random/local address (e.g. smartphone with private MAC, VM, container)"),
        // --- Ereignisse ---
        ("Fehler, kritische Ereignisse und Warnungen der letzten 24 Stunden (System und Anwendungen).", "Errors, critical events and warnings from the last 24 hours (System and Application)."),
        ("Zeit", "Time"), ("Stufe", "Level"), ("Protokoll", "Log"), ("ID", "ID"), ("Meldung", "Message"), ("Fehler", "Error"), ("Kritisch", "Critical"),
        ("{0} Fehler und {1} Warnungen in den letzten 24 Stunden.", "{0} errors and {1} warnings in the last 24 hours."), ("System-Protokoll", "System log"), ("Anwendungen-Protokoll", "Application log"),
        // --- Einstellungen ---
        ("Overlay – Kennzahlen dauerhaft im Blick", "Overlay – key metrics always in view"),
        ("Ein kleines Fenster über allem anderen, durch das man hindurchklickt – wie ein FPS-Overlay. Es nimmt nie den Fokus und liest nur vorhandene Messwerte. Ein/Aus: Strg+Alt+O, Hauptfenster: Strg+Alt+M, Tray-Symbol für Verschieben und Beenden.",
         "A small window above everything else that you can click through – like a game FPS overlay. It never takes focus and only reads existing measurements. On/off: Ctrl+Alt+O, main window: Ctrl+Alt+M, tray icon for moving and quitting."),
        ("Angezeigte Kennzahlen", "Displayed metrics"), ("Position", "Position"), ("Oben rechts", "Top right"), ("Oben links", "Top left"), ("Unten rechts", "Bottom right"), ("Unten links", "Bottom left"),
        ("Frei verschoben", "Custom (dragged)"), ("Anordnung", "Layout"), ("Untereinander", "Stacked"), ("Leiste (in einer Zeile)", "Bar (single row)"), ("Deckkraft", "Opacity"), ("Schriftgröße", "Font size"),
        ("Overlay beim Programmstart anzeigen", "Show overlay at program start"), ("Mit Windows starten (nur Overlay, im Hintergrund)", "Start with Windows (overlay only, in the background)"),
        ("Overlay ein/aus", "Overlay on/off"), ("Overlay verschieben", "Move overlay"), ("Danach das Overlay mit der Maus ziehen und loslassen", "Then drag the overlay with the mouse and release"),
        ("Schwellenwerte", "Thresholds"), ("CPU Warnung / kritisch (%)", "CPU warning / critical (%)"), ("Arbeitsspeicher Warnung / kritisch (%)", "Memory warning / critical (%)"),
        ("Freier Platz Warnung / kritisch (%)", "Free space warning / critical (%)"), ("Maximales Alter der letzten Sicherung (Std.)", "Maximum age of the last backup (hours)"),
        ("Ereignisse (Fehler/24 h) Warnung / kritisch", "Events (errors/24 h) warning / critical"), ("Aktualisierung (Sekunden)", "Refresh interval (seconds)"),
        ("Überwachte Dienste (einer pro Zeile – müssen immer laufen)", "Watched services (one per line – must always run)"),
        ("Weitere Datenbank-Dienste (Muster mit * erlaubt, einer pro Zeile)", "Additional database services (wildcards * allowed, one per line)"),
        ("Datenbank-Verbindungen für Detailwerte (einer pro Zeile)", "Database connections for detail values (one per line)"),
        ("Format: postgresql|Dienstname-Teil|Host=localhost;Username=monitor;Password=…   oder   mysql||Server=localhost;User ID=monitor;Password=…   – am besten ein Benutzer nur mit Leserechten. Wird verschlüsselt (Windows-DPAPI) gespeichert.",
         "Format: postgresql|part of service name|Host=localhost;Username=monitor;Password=…   or   mysql||Server=localhost;User ID=monitor;Password=…   – ideally a read-only user. Stored encrypted (Windows DPAPI)."),
        ("Weitere Stichwörter für Sicherungsaufgaben (einer pro Zeile)", "Additional keywords for backup tasks (one per line)"),
        ("Speichern", "Save"), ("Bericht exportieren …", "Export report …"), ("Sprache", "Language"), ("Automatisch", "Automatic"), ("Gespeichert in {0}", "Saved to {0}"),
        ("Hinweis: Ohne Administratorrechte sind einige Messwerte nicht verfügbar.", "Note: some measurements are not available without administrator rights."),
        ("Keine Kennzahlen gewählt", "No metrics selected"),
        // --- Overlay ---
        ("Gesundheit (Ampel + Wert)", "Health (light + score)"), ("Netzwerk (Down/Up)", "Network (down/up)"),
        ("Wenigster freier Platz", "Least free space"), ("Prozess mit höchster CPU", "Process with highest CPU"), ("Datenbank (Verbindungen, CPU)", "Database (connections, CPU)"),
        ("Remote-Sitzungen", "Remote sessions"), ("SMB-Clients", "SMB clients"), ("Internet-Verbindungen", "Internet connections"), ("Sicherung läuft", "Backup running"),
        ("DHCP: wirklich freie Adressen", "DHCP: really free addresses"), ("Active Directory: gesperrte Konten", "Active Directory: locked-out accounts"),
        ("Health", "Health"), ("gesund", "healthy"), ("Problem", "Problem"), ("Top", "Top"),
        ("Overlay anzeigen   (Strg+Alt+O)", "Show overlay   (Ctrl+Alt+O)"), ("Hauptfenster öffnen   (Strg+Alt+M)", "Open main window   (Ctrl+Alt+M)"), ("Beenden", "Quit"),
        // --- Leistung (Prüfungen) ---
        ("Datenträger & Netzwerk", "Disk & network"), ("{0}% Auslastung (Mittel der letzten {1} s), {2} Prozesse.", "{0}% load (average of the last {1} s), {2} processes."),
        ("{0} GB von {1} GB belegt ({2}%).", "{0} GB of {1} GB used ({2}%)."), ("{0} GB von {1} GB belegt", "{0} GB of {1} GB used"),
        ("Datenträger {0}% ausgelastet, {1}/s · Netzwerk ↓ {2}/s ↑ {3}/s", "Disk {0}% busy, {1}/s · Network ↓ {2}/s ↑ {3}/s"),
        // --- Dienste (Prüfungen) ---
        ("Server-Rollen & Basisdienste", "Server roles & core services"), ("Autostart-Dienste", "Autostart services"), ("Fehlgeschlagene Autostart-Dienste", "Failed autostart services"),
        ("{0} von {1} Diensten laufen, keiner ist mit Fehler beendet.", "{0} of {1} services are running, none has stopped with an error."),
        ("Dienst \"{0}\" ({1}) ist gestoppt, obwohl er automatisch startet.", "Service \"{0}\" ({1}) is stopped although it starts automatically."),
        ("Dienst nicht gefunden.", "Service not found."), ("Läuft.", "Running."), ("Überwacht: {0}", "Watched: {0}"), ("Dienst ist {0}.", "Service is {0}."),
        ("{0} – laufen.", "{0} – running."),
        ("Dateiserver (SMB)", "File server (SMB)"), ("Druckwarteschlange", "Print spooler"), ("Zeitdienst", "Time service"), ("Aufgabenplanung", "Task scheduler"),
        ("Ereignisprotokoll", "Event log"), ("DNS-Client", "DNS client"), ("Windows-Firewall", "Windows Firewall"), ("Microsoft Defender", "Microsoft Defender"),
        ("DNS-Server", "DNS server"), ("Remotedesktop", "Remote Desktop"), ("IIS Webserver", "IIS web server"), ("Kerberos KDC", "Kerberos KDC"), ("DFS-Replikation", "DFS replication"),
        // --- System ---
        ("System", "System"), ("Neustart ausstehend", "Reboot pending"), ("Windows wartet auf einen Neustart (Updates).", "Windows is waiting for a restart (updates)."), ("Kein Neustart erforderlich.", "No restart required."),
        ("Laufwerk {0}", "Drive {0}"), ("{0} GB frei von {1} GB ({2}% frei).", "{0} GB free of {1} GB ({2}% free)."),
        ("Physische Datenträger", "Physical disks"), ("{0} Datenträger melden den Zustand „Gesund“.", "{0} disk(s) report the state “Healthy”."),
        ("{0} Tage {1} Std. seit dem letzten Start.", "{0} days {1} h since the last start."),
        // --- Sicherheit ---
        ("Sicherheit", "Security"), ("Virenschutz (Defender)", "Antivirus (Defender)"), ("Aktiv, Signaturen {0} Tag(e) alt.", "Active, signatures {0} day(s) old."),
        ("Echtzeitschutz ist ausgeschaltet.", "Real-time protection is turned off."),
        ("Windows-Firewall", "Windows Firewall"), ("Alle Profile sind eingeschaltet.", "All profiles are turned on."), ("{0} von {1} Profilen sind ausgeschaltet.", "{0} of {1} profiles are turned off."),
        ("Letzte Update-Installation", "Last update installation"), ("Update-Suche", "Update search"), ("Ausstehende Updates", "Pending updates"),
        ("{0} Update(s) warten auf die Installation.", "{0} update(s) are waiting to be installed."), ("{0} Update(s) warten auf die Installation, {1} davon wichtig oder kritisch.", "{0} update(s) are waiting to be installed, {1} of them important or critical."),
        ("Keine ausstehenden Updates laut letztem Scan.", "No pending updates according to the last scan."), ("Zertifikate", "Certificates"),
        ("{0} Zertifikat(e), keines läuft in den nächsten 30 Tagen ab.", "{0} certificate(s), none expires within the next 30 days."),
        ("Offene Ports", "Open ports"), ("Keiner der geprüften Ports lauscht auf allen Netzwerkkarten.", "None of the checked ports listens on all network adapters."),
        ("{0} Dienst(e) lauschen auf allen Netzwerkkarten (üblich: {1}).", "{0} service(s) listen on all network adapters (usual: {1})."),
        ("Nur mit Administratorrechten", "Administrator rights required"),
        ("Windows-Updates", "Windows Updates"), ("Microsoft Defender", "Microsoft Defender"), ("Zertifikate (lokaler Computer)", "Certificates (local computer)"),
        ("Ausstehende Updates", "Pending updates"), ("Ports, die auf allen Netzwerkkarten lauschen", "Ports listening on all network adapters"),
        ("Ob ein Port aus dem Internet erreichbar ist, hängt von Firewall und Router ab – das wird hier nicht geprüft.", "Whether a port is reachable from the internet depends on firewall and router – this is not checked here."),
        ("Eigenschaft", "Property"), ("Wert", "Value"), ("Profil", "Profile"), ("Quelle", "Source"), ("Port", "Port"), ("Bedeutung", "Meaning"), ("Bewertung", "Rating"),
        ("Update", "Update"), ("Schweregrad", "Severity"), ("Speicher", "Store"), ("Gültig bis", "Valid until"), ("Verbleibend", "Remaining"), ("Aussteller", "Issuer"), ("Schutz", "Protection"),
        ("Letzte Suche nach Updates", "Last update search"), ("Letzte erfolgreiche Installation", "Last successful installation"), ("unbekannt", "unknown"), ("wird ermittelt …", "determining …"),
        ("Virenschutz aktiv", "Antivirus active"), ("Echtzeitschutz", "Real-time protection"), ("Signaturen", "Signatures"), ("Letzter Schnellscan", "Last quick scan"), ("Letzter Vollscan", "Last full scan"),
        ("ja", "yes"), ("nein", "no"), ("an", "on"), ("AUS", "OFF"), ("nie", "never"), ("lokal", "local"), ("Gruppenrichtlinie", "group policy"), ("Domäne", "Domain"), ("Privat", "Private"), ("Öffentlich", "Public"),
        ("üblich", "usual"), ("prüfen", "review"),
        // --- Hardware ---
        ("Hardware", "Hardware"), ("Datenträger-Gesundheit", "Disk health"), ("Speicherplätze", "Storage Spaces"), ("Temperatur", "Temperature"), ("USV / Batterie", "UPS / battery"),
        ("{0} Datenträger ohne Auffälligkeiten (Zustand, Temperatur, Verschleiß, Lesefehler).", "{0} disk(s) without issues (health, temperature, wear, read errors)."),
        ("Datenträger (Zustand, Temperatur, Verschleiß)", "Disks (health, temperature, wear)"), ("Speicherplätze (Storage Spaces)", "Storage Spaces"),
        ("Temperaturen (ACPI-Zonen)", "Temperatures (ACPI zones)"), ("USV / Batterie", "UPS / battery"),
        ("Datenträger", "Disk"), ("Typ", "Type"), ("Größe", "Size"), ("Temperatur", "Temperature"), ("Verschleiß", "Wear"), ("Lesefehler", "Read errors"), ("Betriebsstunden", "Power-on hours"),
        ("Ladung", "Charge"), ("Restlaufzeit", "Runtime left"), ("Netzbetrieb", "mains power"), ("läuft auf Batterie", "on battery"), ("Zone", "Zone"),
        ("Läuft auf Batterie – Stromausfall oder USV im Akkubetrieb.", "Running on battery – power outage or UPS on battery."), ("Netzbetrieb, Batterie geladen.", "On mains power, battery charged."),
        ("gesund", "healthy"), ("defekt", "failed"), ("eingeschränkt", "degraded"), ("Pool", "Pool"), ("Virtueller Datenträger", "Virtual disk"),
        ("{0} Pools/Datenträger gesund.", "{0} pool(s)/disk(s) healthy."), ("Höchste gemeldete Temperatur {0} °C.", "Highest reported temperature {0} °C."),
        // --- Rollen ---
        ("Hyper-V", "Hyper-V"), ("IIS", "IIS"), ("Druckserver", "Print server"), ("Dateiserver", "File server"), ("Exchange", "Exchange"), ("DFS", "DFS"), ("Drucker", "Printers"),
        ("Virtuelle Maschinen", "Virtual machines"), ("{0} von {1} VMs laufen, keine Auffälligkeiten.", "{0} of {1} VMs running, no issues."), ("VM", "VM"),
        ("RAM (MB)", "RAM (MB)"), ("Laufzeit", "Uptime"), ("Replikation", "Replication"), ("Checkpoints", "Checkpoints"), ("Heartbeat", "Heartbeat"),
        ("Webserver", "Web server"), ("Websites", "Websites"), ("Anwendungspools", "Application pools"), ("Website", "Website"), ("Anwendungspool", "Application pool"), (".NET-Version", ".NET version"),
        ("HTTPS-Zertifikate der Bindungen", "HTTPS certificates of the bindings"), ("Bindung", "Binding"), ("Zertifikat", "Certificate"),
        ("{0} Website(s) und {1} Anwendungspool(s) laufen.", "{0} website(s) and {1} application pool(s) are running."),
        ("Druckwarteschlangen", "Print queues"), ("Freigegeben", "Shared"), ("Aufträge", "Jobs"), ("Ältester Auftrag", "Oldest job"),
        ("{0} Drucker, keine hängenden Aufträge.", "{0} printer(s), no stuck jobs."), ("Kontingente (FSRM)", "Quotas (FSRM)"), ("Kontingente", "Quotas"), ("Limit", "Limit"), ("Belegt", "Used"), ("Auslastung", "Utilization"),
        ("{0} Kontingente, alle unter 85 % belegt.", "{0} quota(s), all below 85 % used."), ("Exchange-Dienste", "Exchange services"),
        ("{0} Exchange-Dienste, alle automatischen laufen.", "{0} Exchange services, all automatic ones are running."), ("Mailwarteschlange", "Mail queue"),
        ("{0} Nachrichten in den Zustellwarteschlangen.", "{0} messages in the delivery queues."), ("DFS-Replikation", "DFS replication"), ("Replizierter Ordner", "Replicated folder"),
        ("{0} replizierte Ordner, alle normal.", "{0} replicated folders, all normal."),
        ("Noch keine Daten – die Zusatzprüfungen (Sicherheit, Hardware, Rollen) laufen im Hintergrund und aktualisieren sich alle 5 Minuten.", "No data yet – the additional checks (security, hardware, roles) run in the background and refresh every 5 minutes."),
        ("Wirklich vergebbar: {0} von {1} Adressen", "Really assignable: {0} of {1} addresses"),
        (" · {0} Konflikt(e): antworten, obwohl DHCP sie als frei führt", " · {0} conflict(s): respond although DHCP lists them as free"),
        (" · letzter Scan {0}", " · last scan {0}"), (" · Scan ausstehend", " · scan pending"),
        (".\nDie Zahl „wirklich frei“ = laut DHCP frei und im Scan (Ping und ARP) ohne Antwort.", ".\n“Really free” = free per DHCP and no response in the scan (ping and ARP)."),
        ("Nicht lesbar ohne Administratorrechte: {0}.", "Not readable without administrator rights: {0}."), ("Temperaturen", "Temperatures"), ("BitLocker", "BitLocker"),
        ("{0} Tag(e) alt", "{0} day(s) old"), ("vor {0} Tag(en)", "{0} day(s) ago"),
        ("SMB / Dateifreigabe", "SMB / file sharing"), ("FTP (unverschlüsselt)", "FTP (unencrypted)"), ("Telnet (unverschlüsselt)", "Telnet (unencrypted)"),
        ("Redis (oft ohne Kennwort)", "Redis (often without password)"), ("WinRM (HTTP)", "WinRM (HTTP)"),
        ("Auf allen Netzwerkkarten erreichbar und meist ungeschützt: {0}.", "Reachable on all network adapters and usually unprotected: {0}."),
        ("Nicht lesbar", "Not readable"), ("Messung fehlgeschlagen", "Measurement failed"), ("Abfrage fehlgeschlagen", "Query failed"),
        ("{0} Zertifikat(e) laufen bald ab oder sind kürzlich abgelaufen: {1}", "{0} certificate(s) expire soon or recently expired: {1}"),
        ("{0} Konto/Konten", "{0} account(s)"), ("{0} Tage", "{0} days"), ("abgelaufen", "expired"),
        ("vor {0} Tagen", "{0} days ago"), ("{0} Tagen", "{0} days"),
        ("Prozesse filtern", "Filter processes"), ("Dienste filtern", "Filter services"), ("Verbindungen filtern", "Filter connections"), ("Adressen filtern", "Filter addresses"),
        ("Prozessliste", "Process list"), ("Dienstliste", "Service list"), ("Netzwerkverbindungen", "Network connections"), ("IP-Adressen", "IP addresses"),
        ("DHCP-Bereiche", "DHCP scopes"), ("Ereignisliste", "Event list"), ("CPU-Verlauf", "CPU history"), ("Speicher-Verlauf", "Memory history"),
        ("Datenträger-Verlauf", "Disk history"), ("Netzwerk-Verlauf", "Network history"), ("Navigation", "Navigation"), ("Seite wählen", "Choose page"),
        ("Bildschirm", "Screen"), ("Hauptbildschirm", "Primary screen"), ("Haupt", "primary"),
        ("Server", "Servers"), ("Kategorie", "Category"), ("Alarme & Zugriff", "Alerts & access"), ("Serverliste", "Server list"), ("Prüfungen des Servers", "Checks of the server"),
        ("Übersicht über diesen Rechner und alle entfernten Server, die ihren Status-Zugriff eingeschaltet haben. Auswählen zeigt die Prüfungen des Servers.", "Overview of this computer and all remote servers that have turned on their status access. Selecting one shows its checks."),
        ("Gesundheit", "Health"), ("Stand", "As of"), ("Auffälligkeiten", "Issues"), ("Prüfung", "Check"), ("Nicht erreichbar", "Unreachable"), ("dieser Rechner", "this computer"),
        ("Erreichbarkeit", "Reachability"), ("Server nicht erreichbar", "Server unreachable"), ("Erreichbar.", "Reachable."),
        ("Zertifikat nicht vertraut – Fingerabdruck des Servers eintragen", "Certificate not trusted – enter the server's fingerprint"),
        ("Alarme – melden, wenn etwas kaputtgeht", "Alerts – tell me when something breaks"),
        ("Die App meldet Warnungen und kritische Probleme nach außen (Handy, Teams, Slack …). Es werden nur ausgehende Verbindungen aufgebaut. Ein Problem wird erst gemeldet, wenn es länger als die Verzögerung besteht; gemeldet wird auch, wenn es behoben ist. Die App muss dafür laufen (zum Beispiel als Overlay im Hintergrund).",
         "The app reports warnings and critical problems to the outside (phone, Teams, Slack …). Only outgoing connections are made. A problem is only reported once it lasts longer than the delay; it is also reported when it is resolved. The app must be running for this (for example as a background overlay)."),
        ("Alarme einschalten", "Turn alerts on"), ("Ziele (eines pro Zeile)", "Targets (one per line)"), ("Verzögerung (Min.)", "Delay (min.)"),
        ("Erinnerung alle (Std., 0 = nie)", "Remind every (h, 0 = never)"), ("Höchstens pro Stunde", "At most per hour"),
        ("Nur kritische Probleme melden", "Only report critical problems"), ("Auch melden, wenn ein Problem behoben ist", "Also report when a problem is resolved"),
        ("Testnachricht senden", "Send test message"), ("Sende …", "Sending …"), ("Nicht gespeichert", "Not saved"),
        ("Status-Zugriff (nur lesen)", "Status access (read-only)"), ("Status-Zugriff einschalten", "Turn status access on"), ("Adresse", "Address"), ("Neues Token", "New token"),
        ("Token", "Token"), ("Fingerabdruck des Zertifikats (SHA-1)", "Certificate fingerprint (SHA-1)"), ("Adresse für den Abruf", "Address for access"),
        ("Macht die Zusammenfassung dieses Rechners über HTTPS abrufbar, damit ihn andere Installationen und ein Browser sehen können. Es gibt nur lesenden Zugriff, nur mit Token, ohne Einstellungen und Zugangsdaten. Gedacht für ein VPN (Tailscale, WireGuard): Tragen Sie als Adresse die des VPN-Adapters ein, nicht 0.0.0.0. Die Windows-Firewall öffnet die App nicht von selbst.",
         "Makes this computer's summary available over HTTPS so other installations and a browser can see it. Read-only access only, token required, no settings or credentials. Meant for a VPN (Tailscale, WireGuard): enter the address of the VPN adapter, not 0.0.0.0. The app does not open the Windows firewall by itself."),
        ("Entfernte Server überwachen", "Monitor remote servers"),
        ("Trägt man hier Server ein, die ihren Status-Zugriff eingeschaltet haben, erscheinen sie auf der Seite „Server“; Alarme gelten dann auch für sie, samt Meldung, wenn ein Server nicht mehr antwortet.",
         "Servers entered here that have turned on their status access appear on the “Servers” page; alerts then apply to them as well, including a message when a server stops responding."),
        ("Letzte Alarme", "Recent alerts"), ("Quelle", "Source"), ("Zeit", "Time"), ("Art", "Kind"), ("Meldung", "Message"),
        ("Problem", "Problem"), ("Erinnerung", "Reminder"), ("Behoben", "Resolved"), ("Verschärft", "Escalated"), ("Test", "Test"), ("Mehrere Probleme", "Multiple problems"), ("Limit erreicht", "Limit reached"),
        ("[Test] ServerManagerPro auf {0}", "[Test] ServerManagerPro on {0}"), ("[Limit] {0}: zu viele Meldungen", "[Limit] {0}: too many messages"), ("{0} Meldungen", "{0} messages"),
        ("Dies ist eine Testnachricht. Wenn Sie sie lesen, funktioniert dieses Ziel.", "This is a test message. If you can read it, this target works."),
        ("Es wurden mehr als {0} Meldungen pro Stunde erzeugt. Weitere Meldungen werden in dieser Stunde unterdrückt.", "More than {0} messages per hour were generated. Further messages are suppressed this hour."),
        ("Das Problem bestand {0}.", "The problem lasted {0}."), ("Besteht seit {0}.", "Present for {0}."),
        ("Verlauf", "History"), ("Quelle", "Source"), ("Zeitraum", "Period"), ("Letzte 6 Stunden", "Last 6 hours"), ("Letzte 24 Stunden", "Last 24 hours"),
        ("Letzte 7 Tage", "Last 7 days"), ("Letzte 30 Tage", "Last 30 days"), ("CSV exportieren …", "Export CSV …"), ("Zustand im Zeitraum", "State during the period"),
        ("Gesundheitswert", "Health score"), ("Zustandswechsel", "State changes"), ("Wechsel", "Change"), ("Spitze", "Peak"), ("Tiefstwert", "Lowest"), ("keine Daten", "no data"),
        ("Keine Daten im gewählten Zeitraum", "No data in the selected period"), ("Noch keine Daten.", "No data yet."),
        ("In Ordnung {0} % · Warnung {1} % · Kritisch {2} %  (aus {3} Messpunkten)", "OK {0} % · Warning {1} % · Critical {2} %  (from {3} samples)"),
        ("Der Verlauf ist ausgeschaltet (Einstellungen).", "History is turned off (Settings)."),
        ("Noch keine Daten. Der Verlauf wird aufgezeichnet, solange die App läuft (auch im Hintergrund).", "No data yet. History is recorded while the app is running (also in the background)."),
        ("Daten seit {0} · {1} Minutenwerte im Zeitraum · Speicherplatz {2} · Aufbewahrung {3} Tage. Lücken entstehen, wenn die App nicht lief.", "Data since {0} · {1} one-minute values in the period · storage {2} · retention {3} days. Gaps appear when the app was not running."),
        ("{0} Zeilen exportiert: {1}", "{0} rows exported: {1}"), ("Verlauf aufzeichnen", "Record history"), ("Aufbewahren (Tage)", "Keep (days)"), ("Verlauf löschen", "Delete history"),
        ("Den gesamten aufgezeichneten Verlauf löschen? Das kann nicht rückgängig gemacht werden.", "Delete the entire recorded history? This cannot be undone."), ("seit {0}, {1}", "since {0}, {1}"),
        ("Beim Start", "At start"), ("Quelle des Verlaufs", "History source"), ("Zeitraum des Verlaufs", "History period"), ("Aufbewahrung in Tagen", "Retention in days"),
        ("CPU-Verlauf über die Zeit", "CPU history over time"), ("Speicher-Verlauf über die Zeit", "Memory history over time"), ("Datenträger-Verlauf über die Zeit", "Disk history over time"),
        ("Netzwerk-Verlauf über die Zeit", "Network history over time"), ("Verlauf des Gesundheitswerts", "Health score history"),
        // --- Updates / Allgemein ---
        ("Neue Version {0} verfügbar", "New version {0} available"), ("Die Sprache wird nach einem Neustart der Anwendung übernommen.", "The language will change after the application is restarted."), ("Jetzt aktualisieren", "Update now"), ("Später", "Later"),
        ("Nach Updates suchen", "Check for updates"), ("Automatisch nach Updates suchen (GitHub, einmal täglich)", "Check for updates automatically (GitHub, once a day)"),
        ("Es ist die neueste Version.", "You have the latest version."), ("Ins Tray schließen", "Close to tray"), ("Messung fehlgeschlagen", "Measurement failed"),
    };
}
