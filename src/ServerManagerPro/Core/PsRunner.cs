using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ServerManagerPro.Core;

/// <summary>Führt kurze PowerShell-Skripte aus (nur lesend) und liefert JSON zurück.</summary>
public static class PsRunner
{
    public static string Run(string script, int timeoutSec = 60)
    {
        string full = "$ErrorActionPreference='Stop'\n[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" + script;
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(full)))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } throw new TimeoutException("PowerShell-Abfrage dauerte zu lange."); }
        string o = outp.Result.Trim();
        if (p.ExitCode != 0 || o.Length == 0)
        {
            string e = err.Result.Trim();
            throw new InvalidOperationException(e.Length > 0 ? e.Split('\n')[0].Trim() : "Keine Daten (Modul/Rolle fehlt oder keine Berechtigung).");
        }
        return o;
    }

    public static JsonDocument RunJson(string script, int timeoutSec = 60) => JsonDocument.Parse(Run(script, timeoutSec));

    public static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList()
        : e.ValueKind == JsonValueKind.Object ? new List<JsonElement> { e } : new List<JsonElement>();

    public static string S(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    public static double D(JsonElement e, string n) => double.TryParse(S(e, n), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}
