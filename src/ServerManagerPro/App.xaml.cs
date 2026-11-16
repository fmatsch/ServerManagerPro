using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using ServerManagerPro.Core;

namespace ServerManagerPro;

public partial class App : Application
{
    public static string[] Args = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        Args = e.Args;
        // Kopfloser Selbsttest: ServerManagerPro.exe --selftest <datei.txt>
        int i = Array.IndexOf(Args, "--selftest");
        if (i >= 0)
        {
            string path = i + 1 < Args.Length ? Args[i + 1] : "selftest.txt";
            SelfTest(path);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }

    static void SelfTest(string path)
    {
        var sb = new StringBuilder();
        using var mon = new HealthMonitor(AppSettings.Load());
        var sw = Stopwatch.StartNew();
        mon.FastOnce();
        Thread.Sleep(1500);
        var m = mon.FastOnce();
        sb.AppendLine($"CPU {m.Cpu:0.0}% | RAM {m.MemUsedGb:0.0}/{m.MemTotalGb:0.0} GB | Disk {m.DiskBusy:0}% | Net ↓{Fmt.Bytes(m.NetRxBps)}/s ↑{Fmt.Bytes(m.NetTxBps)}/s | Kerne {m.Cores.Length} | Prozesse {m.Processes}");
        var s = mon.HealthOnce();
        sb.AppendLine($"Admin: {s.IsAdmin} | Gesundheit: {HealthMonitor.Score(s.Checks)} ({HealthMonitor.Overall(s.Checks)}) | {sw.ElapsedMilliseconds} ms");
        foreach (var g in s.Checks.GroupBy(c => c.Category))
        {
            sb.AppendLine($"\n[{g.Key}]");
            foreach (var c in g) sb.AppendLine($"  {c.Status,-9} {c.Name}: {c.Detail}");
        }
        sb.AppendLine($"\nDatenbanken: {s.Databases.Count} | Sicherungen: {s.Backups.Count} | Aufgaben: {s.Tasks.Count} | Sitzungen: {s.Sessions.Count} | Ereignisse: {s.Events.Count}");
        File.WriteAllText(path, sb.ToString());
    }
}
