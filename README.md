# ServerManagerPro

Der **Task-Manager für Server**: CPU, Speicher, Prozesse und Dienste – plus der Gesundheitscheck, den man sonst an zehn Stellen zusammensucht.

- Welche **Dienste** laufen – Server-Rollen (AD, DNS, DHCP, IIS, Hyper-V …) und eigene „muss laufen“-Dienste?
- Welche **Datenbanken** laufen, und wie ausgelastet ist der Datenbankserver (CPU, RAM, Verbindungen, bei SQL Server Batch-Requests/s, Seitenlebensdauer, Blockierungen)?
- Läuft gerade ein **Backup**? Wann war das letzte erfolgreiche?
- Wie viele **Clients** sind am Server (Remotedesktop, SMB, TCP-Verbindungen)?
- Welche **Netzwerkverbindungen** bestehen – getrennt nach lokalem Netz (LAN) und Internet?
- Auf **Domänencontrollern**: Zustand von Active Directory (Dienste, Replikation, Betriebsmaster, Benutzer, gesperrte Konten).
- Auf **DHCP-Servern**: Bereiche, Leases und ein IP-Scan, der zeigt, welche Adressen *wirklich* vergeben werden können.
- **Overlay:** frei gewählte Kennzahlen als kleines, klickdurchlässiges Fenster dauerhaft im Vordergrund – wie ein FPS-Overlay.
- Dazu: Laufwerke, Datenträger-Zustand, ausstehender Neustart, Fehler im Ereignisprotokoll.

Alles fließt in eine Ampel und einen Gesundheitswert von 0–100 – jede Auffälligkeit mit Klartext-Begründung.

Läuft auf **Windows Server 2016/2019/2022/2025** sowie **Windows 10 und 11** (64 Bit), auch auf kleinen Bildschirmen: die Oberfläche passt sich an und funktioniert bis **1024×768** (Mindestgröße des Fensters 800×540). Seite mit Screenshots: <https://fmatsch.ist/ServerManagerPro/>

![Übersicht](docs/img/page0.png)

## Installation

ZIP von der [Release-Seite](https://github.com/fmatsch/ServerManagerPro/releases/latest) laden, entpacken, `ServerManagerPro.exe` starten. Die EXE bringt .NET mit; es muss nichts installiert werden. Für das volle Bild als **Administrator** starten (Button in der Kopfzeile).

## Overlay

Ein kleines Fenster über allem anderen mit den Kennzahlen, die Sie unter *Einstellungen → Overlay* auswählen (Gesundheitswert, CPU, RAM, Datenträger, Netzwerk, freier Platz, Top-Prozess, Datenbank, Remote-Sitzungen, SMB, Internet-Verbindungen, Backup, DHCP, Active Directory).

- klickdurchlässig und ohne Fokus – Maus und Tastatur gehen durch das Overlay hindurch
- **Strg+Alt+O** ein/aus, **Strg+Alt+M** Hauptfenster; Tray-Symbol: verschieben, Hauptfenster, beenden
- Position in jeder Ecke oder frei verschiebbar, untereinander oder als Leiste, Deckkraft und Schriftgröße einstellbar
- läuft nur das Overlay, arbeitet die App mit niedriger Priorität und misst nur das Nötige (gemessen: gut 4 % eines Kerns auf einem 24-Thread-Rechner mit Gesundheitswert)
- Start direkt im Hintergrund: `ServerManagerPro.exe --overlay`; optional mit Windows starten (Einstellungen)
- Hinweis: sichtbar über Fenstern und randlosen Vollbildern, nicht über Programmen im exklusiven Vollbildmodus

![Overlay](docs/img/overlay.png)

## Was erkannt wird

| Bereich | Quelle |
|---|---|
| Leistung | Leistungsindikatoren (CPU gesamt/je Kern, RAM, Datenträger, Netzwerk) |
| Dienste | WMI `Win32_Service`, Rollen-Liste, eigene Überwachungsliste |
| Datenbanken | SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB, Firebird, Redis (Dienste) + TCP-Tabelle; SQL-Server-Leistungsindikatoren |
| Sicherung | Windows Server Backup (Ereignisprotokoll), Veeam, Acronis, Backup Exec, Macrium u. a. (Dienste/Prozesse), geplante Aufgaben mit Backup-Stichwörtern, SQL-Backup-Durchsatz, VSS |
| Netzwerk | `GetExtendedTcpTable` mit Prozessnamen; Einteilung nach privaten Adressbereichen (RFC 1918, Link-Local, CGNAT), Loopback, Internet |
| Active Directory | Dienste (NTDS, KDC …), SYSVOL/NETLOGON, `System.DirectoryServices` (Konten, FSMO, Replikation); nur wenn der Dienst NTDS existiert |
| DHCP | PowerShell-Cmdlets `Get-DhcpServerv4*` (Bereiche, Leases, Reservierungen, Ausschlüsse) + IP-Scan per Ping und ARP; nur wenn der Dienst DHCPServer existiert |
| Clients | Terminaldienste (WTS), `MSFT_SmbSession`, `GetExtendedTcpTable` |
| System | Laufzeit, Neustart ausstehend, Laufwerke, `MSFT_PhysicalDisk` |
| Ereignisse | System- und Anwendungsprotokoll der letzten 24 h |

Schwellenwerte, zusätzliche Dienste und Backup-Stichwörter stehen unter *Einstellungen* und in `C:\ProgramData\ServerManagerPro\settings.json`.

## Grenzen

- Active-Directory- und DHCP-Seiten sind nur mit Beispieldaten (`--demo`) auf einem normalen PC getestet, nicht auf einem echten Domänencontroller/DHCP-Server. Fehlerberichte willkommen.
- Der IP-Scan prüft im eigenen Subnetz per ARP (findet auch Geräte mit blockiertem Ping), in anderen Subnetzen nur per Ping. Pro Bereich werden höchstens 4096 Adressen betrachtet; der Scan läuft alle 10 Minuten oder per Knopf.
- Ohne Administratorrechte fehlen SMB-Clients, SQL-Leistungsindikatoren, manche Aufgaben und das Steuern von Diensten.
- Backup-Erkennung ist heuristisch (Dienste, Prozesse, Aufgaben, Ereignisprotokoll). Fehlt Ihre Lösung, helfen Stichwörter in den Einstellungen.
- Detailwerte gibt es für SQL Server; andere Datenbanken zeigen Zustand, CPU, RAM und Verbindungen.
- Die EXE ist nicht signiert (SmartScreen-Hinweis).

## Selbst bauen

Benötigt das .NET 8 SDK.

```powershell
dotnet build src/ServerManagerPro -c Release
dotnet publish src/ServerManagerPro -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Kopfloser Selbsttest, schreibt alle Prüfungen in eine Textdatei: `ServerManagerPro.exe --selftest bericht.txt`

IP-Bereich scannen (Diagnose): `ServerManagerPro.exe --scantest 192.168.1.1-192.168.1.254 antworten.txt`

Oberfläche mit Beispieldaten für AD/DHCP: `ServerManagerPro.exe --demo`

## Lizenz

MIT – siehe [LICENSE](LICENSE).
