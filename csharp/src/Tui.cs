using System.Globalization;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace VulcanScope;

/// <summary>Interactive terminal dashboard (Spectre.Console) + reusable section renderers.</summary>
public static class Tui
{
    private static readonly string[] PlDays = { "pon", "wt", "śr", "czw", "pt", "sob", "ndz" };

    private static string Esc(string s) => Markup.Escape(s ?? "");

    private static Color GColor(double? v)
    {
        if (v is null) return Color.Grey;
        var x = v.Value;
        if (x >= 4.5) return Color.Green;
        if (x >= 3.5) return Color.Yellow;
        if (x >= 2.5) return Color.Orange1;
        return Color.Red;
    }

    private static string GMarkup(double? v)
    {
        if (v is null) return "grey";
        var x = v.Value;
        if (x >= 4.5) return "green";
        if (x >= 3.5) return "yellow";
        if (x >= 2.5) return "orange1";
        return "red";
    }

    private static string FmtDate(string iso)
    {
        if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return iso;
        int wd = ((int)d.DayOfWeek + 6) % 7;
        return $"{PlDays[wd]} {d:dd.MM.yyyy}";
    }

    private static string Num(double? v, string fmt = "0.00") =>
        v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    // --------------------------------------------------------------- //
    public static async Task<int> RunAsync(string credentialsPath)
    {
        JsonObject vm = new();
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Łączę z Hebe API i pobieram dane…", async _ =>
            {
                var (v, _) = await Exporter.RunAsync(credentialsPath, makeDashboard: false, writeFiles: false);
                vm = v;
            });

        if (Console.IsInputRedirected) // non-interactive (tests, pipes): render once and exit
        {
            Header(vm); Overview(vm); GradesView(vm); AttendanceView(vm); ExamsView(vm);
            return 0;
        }

        while (true)
        {
            AnsiConsole.Clear();
            Header(vm);
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[grey]Wybierz widok:[/]")
                .HighlightStyle(new Style(foreground: Color.MediumPurple2))
                .AddChoices("Przegląd", "Oceny", "Frekwencja", "Plan lekcji", "Sprawdziany", "Wyjście"));
            if (choice == "Wyjście") break;

            AnsiConsole.Clear();
            Header(vm);
            switch (choice)
            {
                case "Przegląd": Overview(vm); break;
                case "Oceny": GradesView(vm); break;
                case "Frekwencja": AttendanceView(vm); break;
                case "Plan lekcji": PlanView(vm); break;
                case "Sprawdziany": ExamsView(vm); break;
            }
            AnsiConsole.MarkupLine("\n[grey]Naciśnij Enter, aby wrócić do menu…[/]");
            Console.ReadLine();
        }
        return 0;
    }

    // --------------------------------------------------------------- //
    public static JsonObject CurrentPeriod(JsonObject vm)
    {
        var grades = vm["grades"].Arr();
        foreach (var p in grades)
            if (p.Bool("current") && p is JsonObject jo) return jo;
        return grades.Count > 0 && grades[^1] is JsonObject last ? last : new JsonObject();
    }

    public static void Header(JsonObject vm)
    {
        AnsiConsole.Write(new FigletText("VulcanScope").Color(Color.MediumPurple));
        var s = vm["student"];
        AnsiConsole.MarkupLine($"[bold]{Esc(s.Str("fullName"))}[/]  ·  klasa {Esc(s.Str("class"))}  ·  {Esc(s.Str("school"))}");
        var sy = vm.Str("meta", "schoolYear", "start");
        var lucky = vm.Num("lucky");
        var line = $"[grey]Rok {(sy.Length >= 4 ? sy[..4] : sy)}[/]";
        if (lucky is > 0) line += $"   [green]🍀 numerek {Esc(Num(lucky, "0"))}[/]";
        AnsiConsole.MarkupLine(line);
        AnsiConsole.WriteLine();
    }

    public static void Overview(JsonObject vm)
    {
        var per = CurrentPeriod(vm);
        var att = vm["attendance"];
        AnsiConsole.Write(new Rule($"[bold mediumpurple]Przegląd — {Esc(per.Str("label"))}[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"Średnia ważona: [{GMarkup(per.Num("overall"))} bold]{Num(per.Num("overall"))}[/]    " +
            $"Frekwencja: [aqua bold]{Num(att.Num("frequency"), "0.0")}%[/]");
        AnsiConsole.WriteLine();

        var subs = per["subjects"].Arr().Where(x => x.Num("average") != null)
            .OrderByDescending(x => x.Num("average")).Take(12).ToList();
        if (subs.Count > 0)
        {
            var bc = new BarChart().Width(64).Label("[bold]Średnie wg przedmiotu[/]").LeftAlignLabel();
            foreach (var s in subs)
                bc.AddItem(Esc(s.Str("name")), Math.Round(s.Num("average")!.Value, 2), GColor(s.Num("average")));
            AnsiConsole.Write(bc);
        }
        AnsiConsole.WriteLine();
        AttendanceBreakdown(att!);
        ExamsView(vm, 5, "Nadchodzące sprawdziany");
    }

    public static void GradesView(JsonObject vm)
    {
        foreach (var per in vm["grades"].Arr())
        {
            if (per is not JsonObject p) continue;
            PrintGradesPeriod(p);
        }
    }

    public static void PrintGradesPeriod(JsonObject per)
    {
        AnsiConsole.Write(new Rule(
            $"[bold mediumpurple]{Esc(per.Str("label"))} — średnia {Num(per.Num("overall"))}[/]").LeftJustified());
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn("Przedmiot");
        t.AddColumn(new TableColumn("Śr.").Centered());
        t.AddColumn(new TableColumn("Przew.").Centered());
        t.AddColumn(new TableColumn("Końc.").Centered());
        t.AddColumn("Oceny");
        foreach (var s in per["subjects"].Arr())
        {
            var avg = s.Num("average");
            var pills = string.Join(" ", J.Nav(s, "grades").Arr().Select(g =>
            {
                var nv = ViewModel.GradeNumeric(g.Str("content"), g.Num("value"));
                return $"[{GMarkup(nv)}]{Esc(g.Str("content"))}[/]";
            }));
            t.AddRow(
                new Markup(Esc(s.Str("name"))),
                new Markup($"[{GMarkup(avg)} bold]{Num(avg)}[/]"),
                new Markup(Esc(s.Str("proposed"))),
                new Markup(Esc(s.Str("final"))),
                new Markup(pills));
        }
        AnsiConsole.Write(t);
    }

    public static void AttendanceView(JsonObject vm) => PrintAttendance(vm["attendance"]!);

    public static void PrintAttendance(JsonNode att)
    {
        AnsiConsole.Write(new Rule($"[bold mediumpurple]Frekwencja — {Num(att.Num("frequency"), "0.0")}%[/]").LeftJustified());
        AttendanceBreakdown(att);
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Przedmiot");
        t.AddColumn(new TableColumn("Ob.").Centered());
        t.AddColumn(new TableColumn("Sp.").Centered());
        t.AddColumn(new TableColumn("Nieob.").Centered());
        t.AddColumn(new TableColumn("%").Centered());
        foreach (var s in att["bySubject"].Arr())
        {
            var pct = s.Num("percent");
            var col = pct >= 90 ? "green" : pct >= 75 ? "yellow" : "red";
            int absAll = (s.Int("absent_exc") ?? 0) + (s.Int("absent_unexc") ?? 0);
            t.AddRow(
                Esc(s.Str("subject")),
                (s.Int("present") ?? 0).ToString(),
                (s.Int("late") ?? 0).ToString(),
                absAll.ToString(),
                $"[{col}]{Num(pct, "0.0")}%[/]");
        }
        AnsiConsole.Write(t);
    }

    private static void AttendanceBreakdown(JsonNode att)
    {
        var b = att["buckets"];
        var bd = new BreakdownChart().Width(64);
        bd.AddItem("Obecność", b.Int("present") ?? 0, Color.Green);
        bd.AddItem("Spóźnienia", b.Int("late") ?? 0, Color.Yellow);
        bd.AddItem("Nieob. uspr.", b.Int("absent_exc") ?? 0, Color.Aqua);
        bd.AddItem("Nieob. nieuspr.", b.Int("absent_unexc") ?? 0, Color.Red);
        AnsiConsole.Write(bd);
        AnsiConsole.WriteLine();
    }

    public static void PlanView(JsonObject vm) => PrintPlan(vm["timetable"].Arr());

    public static void PrintPlan(JsonArray timetable)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        AnsiConsole.Write(new Rule("[bold mediumpurple]Plan lekcji — najbliższe dni[/]").LeftJustified());
        var days = timetable
            .Where(l => string.CompareOrdinal(l.Str("date"), today) >= 0)
            .OrderBy(l => l.Str("date"), StringComparer.Ordinal)
            .GroupBy(l => l.Str("date"))
            .Take(5).ToList();
        if (days.Count == 0) { AnsiConsole.MarkupLine("[grey]Brak nadchodzących lekcji.[/]"); return; }
        foreach (var g in days)
        {
            AnsiConsole.MarkupLine($"\n[bold aqua]{Esc(FmtDate(g.Key))}[/]");
            var t = new Table().Border(TableBorder.Minimal);
            t.AddColumn("Godz."); t.AddColumn("Przedmiot"); t.AddColumn("Sala"); t.AddColumn("Nauczyciel");
            foreach (var l in g.OrderBy(x => x.Int("pos") ?? 0))
            {
                var name = Esc(l.Str("subject"));
                if (l.Bool("cancelled")) name = $"[strikethrough red]{name}[/] [red](odwołane)[/]";
                else if (l.Bool("subst")) name = $"[yellow]{name}[/] [yellow](zastępstwo)[/]";
                t.AddRow($"{Esc(l.Str("start"))}–{Esc(l.Str("end"))}", name, Esc(l.Str("room")), Esc(l.Str("teacher")));
            }
            AnsiConsole.Write(t);
        }
    }

    public static void ExamsView(JsonObject vm, int max = 30, string title = "Sprawdziany")
        => PrintExams(vm["exams"].Arr(), upcomingOnly: true, max, title);

    public static void PrintExams(JsonArray exams, bool upcomingOnly, int max = 50, string title = "Sprawdziany")
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var list = exams.Where(e => !upcomingOnly || string.CompareOrdinal(e.Str("date"), today) >= 0)
                        .Take(max).ToList();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold mediumpurple]{Esc(title)}[/]").LeftJustified());
        if (list.Count == 0) { AnsiConsole.MarkupLine("[grey]Brak.[/]"); return; }
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn("Data"); t.AddColumn("Przedmiot"); t.AddColumn("Typ"); t.AddColumn("Opis");
        foreach (var e in list)
            t.AddRow(Esc(FmtDate(e.Str("date"))), Esc(e.Str("subject")), Esc(e.Str("type")), Esc(e.Str("content")));
        AnsiConsole.Write(t);
    }
}
