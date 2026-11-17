namespace ServerManagerPro.Core;

/// <summary>Erzeugt für --demo einen glaubwürdigen Verlauf (nur im Temp-Ordner, nie in den echten Daten).</summary>
public static class DemoHistory
{
    public static void Seed()
    {
        HistoryStore.Root = Path.Combine(Path.GetTempPath(), "smp-demo-history");
        try { if (Directory.Exists(HistoryStore.Root)) Directory.Delete(HistoryStore.Root, true); } catch { }
        Directory.CreateDirectory(HistoryStore.Root);
        Generate("local", 7, big: true);
        Generate("SRV-FILE01", 21, big: false);
    }

    static void Generate(string source, int seed, bool big)
    {
        var rnd = new Random(seed);
        var now = DateTime.Now;
        var start = now.AddDays(-30);
        double mem = 52;
        var changes = new List<ChangeRec>();
        string prevLevel = "In Ordnung";
        var lines = new List<(DateTime, Sample)>();
        for (var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0); t < now; t = t.AddMinutes(t > now.AddHours(-24) ? 1 : 5))
        {
            double hour = t.Hour + t.Minute / 60.0;
            bool work = t.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && hour is >= 7.5 and <= 18;
            double cpu = (work ? 28 + 14 * Math.Sin((hour - 8) / 10 * Math.PI) : 9) + rnd.NextDouble() * 7;
            double disk = (work ? 7 : 3) + rnd.NextDouble() * 6;
            double rx = (work ? 1.2e6 : 2e5) * (0.5 + rnd.NextDouble()), tx = rx * 0.35;
            if (hour is >= 2 and < 3.2) { disk = 62 + rnd.NextDouble() * 25; rx = 4.5e7 * (0.7 + rnd.NextDouble() * 0.4); tx = 2e6; cpu += 14; }   // nächtliche Sicherung
            // Vorfall vor 5 Tagen
            double age = (now - t).TotalDays;
            bool incident = big && age is > 5.0 and < 5.04;
            if (incident) { cpu = 94 + rnd.NextDouble() * 5; disk = 80 + rnd.NextDouble() * 15; }
            // Speicher: langsamer Anstieg mit Neustart alle ~9 Tage
            mem += 0.004 * (t.DayOfYear % 2 == 0 ? 1 : 1) + (rnd.NextDouble() - 0.48) * 0.35;
            if (age is > 21.0 and < 21.01 || age is > 12.0 and < 12.01 || age is > 3.0 and < 3.01) mem = 51;
            mem = Math.Clamp(mem, 40, 93);
            // Ausfall (Lücke) vor 2 Tagen
            if (big && age is > 2.0 and < 2.0 + 30.0 / 1440) continue;
            double score = 100; int overall = 0;
            if (incident) { score = 66; overall = 2; }
            else if (mem > 86) { score = 92; overall = 1; }
            if (source != "local") { score = age is > 1.0 and < 1.4 ? 74 : 100; overall = score < 100 ? 1 : 0; }
            double maxMul = 1 + rnd.NextDouble() * 0.5;
            var s = new Sample(t, cpu, Math.Min(100, cpu * maxMul + 4), mem, Math.Min(100, mem + 1.5), disk, Math.Min(100, disk * maxMul + 8),
                big ? rx : double.NaN, big ? tx : double.NaN, score, overall, big ? 6 + rnd.Next(0, 5) : double.NaN, double.NaN);
            if (!big) s = s with { DiskAvg = double.NaN, DiskMax = double.NaN };
            lines.Add((t, s));

            string level = overall == 2 ? "Kritisch" : overall == 1 ? "Warnung" : "In Ordnung";
            if (level != prevLevel)
            {
                changes.Add(new ChangeRec { Time = t, Source = source, From = prevLevel, To = level, Category = big ? (overall == 2 ? "Leistung" : "Leistung") : "System", Name = big ? (overall == 2 ? "Prozessor" : "Arbeitsspeicher") : "Laufwerk D:", Detail = overall == 2 ? "97 % Auslastung (Mittel der letzten 10 s), 311 Prozesse." : overall == 1 ? "Weniger als 15 % frei." : "Wieder im Normalbereich." });
                prevLevel = level;
            }
        }
        foreach (var (_, s) in lines) HistoryStore.Append(source, s);
        if (big)
        {
            changes.Add(new ChangeRec { Time = now.AddDays(-9).AddHours(3.2), Source = source, From = "In Ordnung", To = "Warnung", Category = "Sicherung", Name = "Letzte erfolgreiche Sicherung", Detail = "gestern 01:58 (vor 38 Std.); erlaubt sind 36 Stunden." });
            changes.Add(new ChangeRec { Time = now.AddDays(-9).AddHours(5), Source = source, From = "Warnung", To = "In Ordnung", Category = "Sicherung", Name = "Letzte erfolgreiche Sicherung", Detail = "heute 03:12 (vor 2 Std.)." });
            changes.Add(new ChangeRec { Time = now.AddDays(-2).AddMinutes(-4), Source = source, From = "In Ordnung", To = "Kritisch", Category = "Dienste", Name = "Datenbankserver", Detail = "SQL Server – SQLEXPRESS ist gestoppt." });
            changes.Add(new ChangeRec { Time = now.AddDays(-2).AddMinutes(26), Source = source, From = "Kritisch", To = "In Ordnung", Category = "Dienste", Name = "Datenbankserver", Detail = "SQL Server – SQLEXPRESS läuft." });
            changes.Add(new ChangeRec { Time = now.AddDays(-14), Source = source, From = "In Ordnung", To = "Warnung", Category = "Sicherheit", Name = "Zertifikate", Detail = "1 Zertifikat(e) laufen bald ab: mail.firma.example (12 T)" });
        }
        foreach (var c in changes.OrderBy(c => c.Time)) HistoryStore.AppendChange(c);
    }
}
