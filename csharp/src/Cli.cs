using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace VulcanScope;

/// <summary>Hand-rolled command dispatcher (zero external CLI deps) + command implementations.</summary>
public static class Cli
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ---------- options ----------
    public sealed class Opts
    {
        public string Command = "";
        public List<string> Positional = new();
        public Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase);
        public bool Has(string f) => Flags.Contains(f);
        public string? Get(string k) => Values.TryGetValue(k, out var v) ? v : null;
        public int? GetInt(string k) => int.TryParse(Get(k), out var v) ? v : null;
    }

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    { "json", "upcoming", "all", "no-dashboard", "help", "once", "selftest" };

    public static Opts Parse(string[] args)
    {
        var o = new Opts();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string? inline = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { inline = key[(eq + 1)..]; key = key[..eq]; }
                if (inline != null) o.Values[key] = inline;
                else if (KnownFlags.Contains(key)) o.Flags.Add(key);
                else if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) o.Values[key] = args[++i];
                else o.Flags.Add(key);
            }
            else if (a.Length == 2 && a[0] == '-')
            {
                string key = a[1] switch
                { 'c' => "credentials", 'p' => "period", 'w' => "week", 'o' => "out", 'j' => "json", _ => a[1].ToString() };
                if (key == "json") o.Flags.Add("json");
                else if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) o.Values[key] = args[++i];
                else o.Flags.Add(key);
            }
            else if (o.Command.Length == 0) o.Command = a;
            else o.Positional.Add(a);
        }
        return o;
    }

    // ---------- entry ----------
    public static async Task<int> Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* ignore on redirected streams */ }
        var o = Parse(args);
        try
        {
            return o.Command.ToLowerInvariant() switch
            {
                "" or "help" => PrintHelp(),
                "export" => await CmdExport(o),
                "tui" or "dashboard" => await Tui.RunAsync(Roots.CredentialsPath(o.Get("credentials"))),
                "grades" or "oceny" => await CmdGrades(o),
                "attendance" or "frekwencja" => await CmdAttendance(o),
                "plan" => await CmdPlan(o),
                "exams" or "sprawdziany" => await CmdExams(o),
                "lucky" or "numerek" => await CmdLucky(o),
                "register" => await Register.RunAsync(o),
                _ => Unknown(o.Command),
            };
        }
        catch (HebeException ex) { AnsiConsole.MarkupLine($"[red]Hebe API:[/] {Markup.Escape(ex.Message)}"); return 2; }
        catch (FileNotFoundException ex) { AnsiConsole.MarkupLine($"[red]Brak pliku:[/] {Markup.Escape(ex.Message)}"); return 3; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Błąd:[/] {Markup.Escape(ex.Message)}"); return 1; }
    }

    private static int Unknown(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]Nieznana komenda:[/] {Markup.Escape(cmd)}");
        return PrintHelp();
    }

    private static int PrintHelp()
    {
        AnsiConsole.Write(new FigletText("VulcanScope").Color(Color.MediumPurple));
        AnsiConsole.MarkupLine("[bold]Eksport dziennika eduVulcan/Hebe — CLI + tryb terminalowy[/]\n");
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Komenda"); t.AddColumn("Opis");
        t.AddRow("register [[--ap-file F]]", "Sparuj konto (bez Pythona) — keygen + register/jwt");
        t.AddRow("export [[--no-dashboard]]", "Pobierz wszystko → data/*.json + dashboard.html");
        t.AddRow("tui", "Interaktywny dashboard w terminalu");
        t.AddRow("grades [[-p N]] [[--json]]", "Oceny (semestr N) — tabela lub JSON");
        t.AddRow("attendance [[--json]]", "Frekwencja + statystyki");
        t.AddRow("plan [[--json]]", "Plan lekcji (najbliższe dni)");
        t.AddRow("exams [[--all]] [[--json]]", "Sprawdziany (domyślnie nadchodzące)");
        t.AddRow("lucky [[--json]]", "Szczęśliwy numerek");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("\n[grey]Opcje globalne:[/] -c|--credentials <plik>   --json (wyjście do potoku/większej aplikacji)");
        return 0;
    }

    // ---------- helpers ----------
    private static async Task<T> WithSpinner<T>(bool show, string msg, Func<Task<T>> fn)
    {
        if (!show) return await fn();
        T result = default!;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync(msg, async _ => { result = await fn(); });
        return result;
    }

    private static VulcanClient Client(Opts o) => new(Roots.CredentialsPath(o.Get("credentials")));

    // ---------- commands ----------
    private static async Task<int> CmdExport(Opts o)
    {
        var creds = Roots.CredentialsPath(o.Get("credentials"));
        List<Exporter.FetchLog> log = new();
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync("Pobieram dane z Hebe API…", async _ =>
            {
                var r = await Exporter.RunAsync(creds, makeDashboard: !o.Has("no-dashboard"), writeFiles: true);
                log = r.Log;
            });

        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Zasób"); t.AddColumn(new TableColumn("#").RightAligned()); t.AddColumn("Status");
        foreach (var l in log)
            t.AddRow(Markup.Escape(l.Name), l.Count.ToString(),
                l.Ok ? "[green]✓ ok[/]" : $"[red]✗ {Markup.Escape(l.Error ?? "")}[/]");
        AnsiConsole.Write(t);

        var root = Path.GetDirectoryName(Path.GetFullPath(creds))!;
        AnsiConsole.MarkupLine($"\n[green]✓[/] data/dashboard_data.json" + (o.Has("no-dashboard") ? "" : " + dashboard.html"));
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(root)}[/]");
        return 0;
    }

    private static async Task<int> CmdGrades(Opts o)
    {
        using var c = Client(o);
        int? pid = null;
        if (o.GetInt("period") is int num)
            foreach (var p in c.Periods)
                if ((p.Int("Number") ?? 0) == num) pid = p.Int("Id");

        var bundles = await WithSpinner(!o.Has("json"), "Pobieram oceny…", () => Exporter.FetchBundlesAsync(c, pid));
        var grades = ViewModel.BuildGrades(bundles);
        if (o.Has("json")) { Console.WriteLine(grades.ToJsonString(Pretty)); return 0; }
        foreach (var per in grades)
            if (per is JsonObject p) Tui.PrintGradesPeriod(p);
        return 0;
    }

    private static async Task<int> CmdAttendance(Opts o)
    {
        using var c = Client(o);
        var att = ViewModel.BuildAttendance(
            await WithSpinner(!o.Has("json"), "Pobieram frekwencję…", () => Exporter.FetchAttendanceAsync(c)));
        if (o.Has("json")) { Console.WriteLine(att.ToJsonString(Pretty)); return 0; }
        Tui.PrintAttendance(att);
        return 0;
    }

    private static async Task<int> CmdPlan(Opts o)
    {
        using var c = Client(o);
        var tt = ViewModel.BuildTimetable(
            await WithSpinner(!o.Has("json"), "Pobieram plan lekcji…", () => Exporter.FetchTimetableAsync(c)));
        if (o.Has("json")) { Console.WriteLine(tt.ToJsonString(Pretty)); return 0; }
        Tui.PrintPlan(tt);
        return 0;
    }

    private static async Task<int> CmdExams(Opts o)
    {
        using var c = Client(o);
        var (ys, ye) = c.YearRange();
        var ex = ViewModel.BuildExams(
            (await WithSpinner(!o.Has("json"), "Pobieram sprawdziany…", () => c.GetExamsAsync(ys, ye))).Arr());
        bool upcoming = !o.Has("all");
        if (o.Has("json"))
        {
            if (!upcoming) { Console.WriteLine(ex.ToJsonString(Pretty)); return 0; }
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var f = new JsonArray();
            foreach (var e in ex)
                if (e is not null && string.CompareOrdinal(e.Str("date"), today) >= 0) f.Add(J.Clone(e));
            Console.WriteLine(f.ToJsonString(Pretty));
            return 0;
        }
        Tui.PrintExams(ex, upcoming, 100, upcoming ? "Nadchodzące sprawdziany" : "Wszystkie sprawdziany");
        return 0;
    }

    private static async Task<int> CmdLucky(Opts o)
    {
        using var c = Client(o);
        var n = await c.GetLuckyNumberAsync();
        if (o.Has("json"))
        {
            Console.WriteLine($"{{\"lucky\": {(n is > 0 ? ((int)n.Value).ToString() : "null")}}}");
            return 0;
        }
        AnsiConsole.MarkupLine(n is > 0
            ? $"🍀 Szczęśliwy numerek: [bold green]{(int)n.Value}[/]"
            : "[grey]Brak szczęśliwego numerka na dziś.[/]");
        return 0;
    }
}
