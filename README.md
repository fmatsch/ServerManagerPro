# ServerManagerPro

Der **Task-Manager für Server**: CPU, Speicher, Prozesse und Dienste – plus der Gesundheitscheck, den man sonst an zehn Stellen zusammensucht.

- Welche **Dienste** laufen – Server-Rollen (AD, DNS, DHCP, IIS, Hyper-V …) und eigene „muss laufen“-Dienste?
- Welche **Datenbanken** laufen, und wie ausgelastet ist der Datenbankserver (CPU, RAM, Verbindungen, bei SQL Server Batch-Requests/s, Seitenlebensdauer, Blockierungen)?
- Läuft gerade ein **Backup**? Wann war das letzte erfolgreiche?
- Wie viele **Clients** sind am Server (Remotedesktop, SMB, TCP-Verbindungen)?
- Dazu: Laufwerke, Datenträger-Zustand, ausstehender Neustart, Fehler im Ereignisprotokoll.

Alles fließt in eine Ampel und einen Gesundheitswert von 0–100 – jede Auffälligkeit mit Klartext-Begründung.

Läuft auf **Windows Server 2016/2019/2022/2025** sowie **Windows 10 und 11** (64 Bit). Seite mit Screenshots: <https://fmatsch.ist/ServerManagerPro/>

![Übersicht](docs/img/page0.png)

## Installation

ZIP von der [Release-Seite](https://github.com/fmatsch/ServerManagerPro/releases/latest) laden, entpacken, `ServerManagerPro.exe` starten. Die EXE bringt .NET mit; es muss nichts installiert werden. Für das volle Bild als **Administrator** starten (Button in der Kopfzeile).

## Was erkannt wird

| Bereich | Quelle |
|---|---|
| Leistung | Leistungsindikatoren (CPU gesamt/je Kern, RAM, Datenträger, Netzwerk) |
| Dienste | WMI `Win32_Service`, Rollen-Liste, eigene Überwachungsliste |
| Datenbanken | SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, MongoDB, Firebird, Redis (Dienste) + TCP-Tabelle; SQL-Server-Leistungsindikatoren |
| Sicherung | Windows Server Backup (Ereignisprotokoll), Veeam, Acronis, Backup Exec, Macrium u. a. (Dienste/Prozesse), geplante Aufgaben mit Backup-Stichwörtern, SQL-Backup-Durchsatz, VSS |
| Clients | Terminaldienste (WTS), `MSFT_SmbSession`, `GetExtendedTcpTable` |
| System | Laufzeit, Neustart ausstehend, Laufwerke, `MSFT_PhysicalDisk` |
| Ereignisse | System- und Anwendungsprotokoll der letzten 24 h |

Schwellenwerte, zusätzliche Dienste und Backup-Stichwörter stehen unter *Einstellungen* und in `C:\ProgramData\ServerManagerPro\settings.json`.

## Grenzen

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

## Lizenz

MIT – siehe [LICENSE](LICENSE).
