using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ServerManagerPro.Core;

/// <summary>
/// Sucht bei GitHub nach einer neueren Version. Es werden keine Daten über Sie oder den Rechner gesendet – nur eine
/// anonyme Abfrage der Release-Liste. Die Installation prüft die SHA-256-Summe des Setups, bevor es startet.
/// </summary>
public static class UpdateChecker
{
    const string Repo = "fmatsch/ServerManagerPro";

    public sealed record Info(string Tag, Version Version, string PageUrl, string? SetupUrl, string? SetupName, string? SumsUrl);

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    static HttpClient NewClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ServerManagerPro-UpdateCheck");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Liefert die neueste Version oder null, wenn die laufende schon die neueste ist.</summary>
    public static async Task<Info?> CheckAsync()
    {
        var info = await LatestAsync();
        return info != null && info.Version > Current ? info : null;
    }

    /// <summary>Die neueste veröffentlichte Version (unabhängig von der laufenden).</summary>
    public static async Task<Info?> LatestAsync()
    {
        using var http = NewClient();
        string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        string? setup = null, setupName = null, sums = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "", url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { setup = url; setupName = name; }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            }
        return new Info(tag, v, root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases/latest", setup, setupName, sums);
    }

    /// <summary>Läuft diese EXE aus einem Installationsordner (Programme)? Dann kann das Setup sie ersetzen.</summary>
    public static bool IsInstalledCopy
    {
        get
        {
            string? p = Environment.ProcessPath;
            if (p == null) return false;
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(f);
                if (root.Length > 0 && p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>Lädt das Setup herunter und prüft die SHA-256-Summe. Gibt den Dateipfad zurück oder wirft eine Ausnahme.</summary>
    public static async Task<string> DownloadSetupAsync(Info info)
    {
        if (info.SetupUrl == null || info.SumsUrl == null || info.SetupName == null) throw new InvalidOperationException("Das Release enthält kein Setup mit Prüfsumme.");
        using var http = NewClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        string sums = await http.GetStringAsync(info.SumsUrl);
        string? expected = sums.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(info.SetupName, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Split(' ', 2)[0]).FirstOrDefault();
        if (string.IsNullOrEmpty(expected)) throw new InvalidOperationException("Keine Prüfsumme für das Setup gefunden – Installation abgebrochen.");

        string dir = Path.Combine(Path.GetTempPath(), "ServerManagerPro-Update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, info.SetupName);
        await using (var src = await http.GetStreamAsync(info.SetupUrl))
        await using (var dst = File.Create(file))
            await src.CopyToAsync(dst);

        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(file);
        string actual = Convert.ToHexString(await sha.ComputeHashAsync(fs));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException("Die Prüfsumme des Downloads stimmt nicht – Installation abgebrochen.");
        }
        return file;
    }

    public static void RunSetup(string file) =>
        Process.Start(new ProcessStartInfo(file, "/SILENT /SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });

    public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
