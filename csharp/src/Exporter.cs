using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VulcanScope;

public static class Exporter
{
    private const int ChunkDays = 28;

    public sealed record FetchLog(string Name, bool Ok, int Count, string? Error);

    // ---- shared fetch helpers (used by export + individual commands) ----
    public static async Task<List<PeriodBundle>> FetchBundlesAsync(VulcanClient c, int? onlyPeriodId = null)
    {
        var bundles = new List<PeriodBundle>();
        foreach (var p in c.Periods)
        {
            int pid = p.Int("Id") ?? 0;
            if (onlyPeriodId.HasValue && pid != onlyPeriodId.Value) continue;
            int num = p.Int("Number") ?? 0, lvl = p.Int("Level") ?? 0;
            var grades = (await c.GetGradesAsync(pid)).Arr();
            JsonArray summary;
            try { summary = (await c.GetGradesSummaryAsync(pid)).Arr(); }
            catch { summary = new JsonArray(); }
            bundles.Add(new PeriodBundle(pid, num, lvl, $"Semestr {num}",
                p.Bool("Current"), p.Str("Start", "Date"), p.Str("End", "Date"), grades, summary));
        }
        return bundles;
    }

    public static async Task<JsonArray> FetchTimetableAsync(VulcanClient c)
    {
        var all = new JsonArray();
        foreach (var p in c.Periods)
            foreach (var (f, t) in Windows(p.Str("Start", "Date"), p.Str("End", "Date")))
                foreach (var l in (await c.GetScheduleChangesAsync(f, t, p.Int("Id") ?? 0)).Arr())
                    if (l is not null) all.Add(J.Clone(l));
        Dedup(all);
        return all;
    }

    public static async Task<JsonArray> FetchAttendanceAsync(VulcanClient c)
    {
        var (ys, ye) = c.YearRange();
        var all = new JsonArray();
        foreach (var (f, t) in Windows(ys, ye))
            foreach (var r in (await c.GetAttendanceAsync(f, t)).Arr())
                if (r is not null) all.Add(J.Clone(r));
        Dedup(all);
        return all;
    }

    // ---- full export ----
    public static async Task<(JsonObject Vm, List<FetchLog> Log)> RunAsync(
        string credentialsPath, bool makeDashboard, bool writeFiles)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(credentialsPath))!;
        using var client = new VulcanClient(credentialsPath);
        var status = new JsonObject();
        var log = new List<FetchLog>();

        void Ok(string n, int c) { status[n] = "ok"; log.Add(new FetchLog(n, true, c, null)); }
        void Fail(string n, string e) { status[n] = "error: " + e; log.Add(new FetchLog(n, false, 0, e)); }

        async Task<JsonArray> Safe(string name, Func<Task<JsonNode?>> fn)
        {
            try { var a = (await fn()).Arr(); Ok(name, a.Count); return a; }
            catch (Exception ex) { Fail(name, ex.Message); return new JsonArray(); }
        }

        double? lucky = null;
        try { lucky = await client.GetLuckyNumberAsync(); Ok("lucky", lucky is > 0 ? 1 : 0); }
        catch (Exception ex) { Fail("lucky", ex.Message); }

        var bundles = new List<PeriodBundle>();
        foreach (var p in client.Periods)
        {
            int pid = p.Int("Id") ?? 0, num = p.Int("Number") ?? 0, lvl = p.Int("Level") ?? 0;
            var grades = await Safe($"grades[{num}]", () => client.GetGradesAsync(pid));
            var summary = await Safe($"summary[{num}]", () => client.GetGradesSummaryAsync(pid));
            bundles.Add(new PeriodBundle(pid, num, lvl, $"Semestr {num}",
                p.Bool("Current"), p.Str("Start", "Date"), p.Str("End", "Date"), grades, summary));
        }

        var (yStart, yEnd) = client.YearRange();

        var timetable = new JsonArray();
        try
        {
            foreach (var p in client.Periods)
                foreach (var (f, t) in Windows(p.Str("Start", "Date"), p.Str("End", "Date")))
                    foreach (var l in (await client.GetScheduleChangesAsync(f, t, p.Int("Id") ?? 0)).Arr())
                        if (l is not null) timetable.Add(J.Clone(l));
            Dedup(timetable); Ok("scheduleChanges", timetable.Count);
        }
        catch (Exception ex) { Fail("scheduleChanges", ex.Message); }

        var attendance = new JsonArray();
        try
        {
            foreach (var (f, t) in Windows(yStart, yEnd))
                foreach (var r in (await client.GetAttendanceAsync(f, t)).Arr())
                    if (r is not null) attendance.Add(J.Clone(r));
            Dedup(attendance); Ok("attendance", attendance.Count);
        }
        catch (Exception ex) { Fail("attendance", ex.Message); }

        var exams = await Safe("exams", () => client.GetExamsAsync(yStart, yEnd));
        var homework = await Safe("homework", () => client.GetHomeworkAsync(yStart, yEnd));
        var notes = await Safe("notes", client.GetNotesAsync);

        var vm = ViewModel.Build(client.Pupil, client.Periods, lucky, bundles,
            timetable, attendance, exams, homework, notes, status);

        if (writeFiles)
        {
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var pretty = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(Path.Combine(dataDir, "dashboard_data.json"), vm.ToJsonString(pretty));
        }

        if (makeDashboard)
        {
            var tpl = Path.Combine(root, "web", "template.html");
            if (File.Exists(tpl))
            {
                var html = File.ReadAllText(tpl);
                var payload = vm.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                                .Replace("</", "<\\/");
                html = html.Replace("/*__DATA__*/null/*__DATA__*/", payload);
                File.WriteAllText(Path.Combine(root, "dashboard.html"), html);
            }
        }

        return (vm, log);
    }

    // ---- date helpers ----
    public static IEnumerable<(string From, string To)> Windows(string start, string end, int days = ChunkDays)
    {
        if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end)) yield break;
        var cur = DateTime.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = DateTime.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        while (cur <= last)
        {
            var we = cur.AddDays(days - 1);
            if (we > last) we = last;
            yield return (cur.ToString("yyyy-MM-dd"), we.ToString("yyyy-MM-dd"));
            cur = we.AddDays(1);
        }
    }

    private static void Dedup(JsonArray arr)
    {
        var seen = new HashSet<int>();
        var keep = new List<JsonNode>();
        foreach (var n in arr)
        {
            if (n is null) continue;
            var id = n.Int("Id");
            if (id is null || seen.Add(id.Value)) keep.Add(n);
        }
        arr.Clear();
        foreach (var n in keep) arr.Add(n);
    }
}
