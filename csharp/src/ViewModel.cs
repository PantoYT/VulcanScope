using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VulcanScope;

public sealed record PeriodBundle(
    int PeriodId, int Number, int Level, string Label, bool Current,
    string Start, string End, JsonArray Grades, JsonArray Summary);

/// <summary>Builds the compact dashboard view-model — same shape the HTML template expects.</summary>
public static class ViewModel
{
    private const double Plus = 0.5;
    private const double Minus = -0.25;

    // ---- grade math (mirror of the Python/JS logic, +/- as +0.5/-0.25) ----
    public static double? GradeNumeric(string content, double? value)
    {
        if (value is null || value <= 0 || value > 6) return null;
        var v = value.Value;
        var c = content.Trim();
        if (c.EndsWith('+')) v += Plus;
        else if (c.EndsWith('-')) v += Minus;
        return v;
    }

    private static (double? val, double w) GradePoints(JsonNode g)
    {
        var v = GradeNumeric(g.Str("Content"), g.Num("Value"));
        if (v is null) return (null, 0);
        var w = g.Num("Column", "Weight") ?? 0;
        if (w <= 0) return (null, 0);
        return (v, w);
    }

    public static double? WeightedAvg(IEnumerable<JsonNode?> grades)
    {
        double num = 0, den = 0;
        foreach (var g in grades)
        {
            if (g is null) continue;
            var (val, w) = GradePoints(g);
            if (val is null) continue;
            num += val.Value * w; den += w;
        }
        return den > 0 ? Math.Round(num / den, 2) : null;
    }

    private static JsonObject CompactGrade(JsonNode g) => new()
    {
        ["content"] = g.Str("Content"),
        ["value"] = g.Num("Value") is double dv ? dv : null,
        ["weight"] = g.Num("Column", "Weight") ?? 0,
        ["name"] = g.Str("Column", "Name"),
        ["category"] = g.Str("Column", "Category", "Name"),
        ["date"] = g.Str("DateCreated", "Date"),
        ["dateDisp"] = g.Str("DateCreated", "DateDisplay"),
        ["teacher"] = g.Str("Creator", "DisplayName"),
        ["comment"] = g.Str("Comment"),
    };

    private static bool? NBool(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static string AttCat(JsonNode? pt)
    {
        if (pt is null) return "other";
        bool? pres = NBool(pt, "Presence"), abs = NBool(pt, "Absence"), late = NBool(pt, "Late"),
              just = NBool(pt, "AbsenceJustified"), legal = NBool(pt, "LegalAbsence");
        if (pres is null && abs is null && late is null && just is null && legal is null) return "other";
        if (late == true) return "late";
        if (abs == true) return (just == true || legal == true) ? "absent_exc" : "absent_unexc";
        if (pres == true) return "present";
        return "neutral";
    }

    // ---- sub-builders ----
    public static JsonObject BuildStudent(JsonObject pupil)
    {
        var p = pupil["Pupil"];
        var unit = pupil["Unit"];
        var homeroom = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ed in pupil["Educators"].Arr())
            foreach (var role in J.Nav(ed, "Roles").Arr())
                if (role.Str("RoleName").Contains("ychowawca"))
                    homeroom.Add((ed.Str("Name") + " " + ed.Str("Surname")).Trim());

        var caps = new JsonArray();
        foreach (var c in pupil["Capabilities"].Arr()) caps.Add(c.Str());
        var hr = new JsonArray();
        foreach (var h in homeroom) hr.Add(h);

        var school = unit.Str("DisplayName");
        if (school.Length == 0) school = unit.Str("Name");

        return new JsonObject
        {
            ["firstName"] = p.Str("FirstName"),
            ["secondName"] = p.Str("SecondName"),
            ["surname"] = p.Str("Surname"),
            ["fullName"] = (p.Str("FirstName") + " " + p.Str("Surname")).Trim(),
            ["class"] = pupil.Str("ClassDisplay"),
            ["info"] = pupil.Str("InfoDisplay"),
            ["school"] = school,
            ["schoolShort"] = unit.Str("Short"),
            ["address"] = unit.Str("Address"),
            ["symbol"] = unit.Str("Symbol"),
            ["homeroom"] = hr,
            ["capabilities"] = caps,
            ["yearStart"] = pupil.Str("Journal", "StartAt"),
            ["yearEnd"] = pupil.Str("Journal", "EndAt"),
            ["pupilNumber"] = pupil.Int("Journal", "PupilNumber") is int pn ? pn : null,
        };
    }

    public static JsonArray BuildGrades(List<PeriodBundle> bundles)
    {
        var outArr = new JsonArray();
        foreach (var b in bundles)
        {
            var summ = new Dictionary<int, JsonNode>();
            foreach (var s in b.Summary)
            {
                var sid = s.Int("Subject", "Id");
                if (sid.HasValue && s is not null) summ[sid.Value] = s;
            }

            var groups = new Dictionary<int, (string name, int pos, List<JsonNode> grades)>();
            foreach (var g in b.Grades)
            {
                if (g is null) continue;
                var subj = J.Nav(g, "Column", "Subject");
                var sid = subj.Int("Id") ?? -1;
                if (!groups.TryGetValue(sid, out var grp))
                {
                    grp = (subj.Str("Name"), subj.Int("Position") ?? 999, new List<JsonNode>());
                    groups[sid] = grp;
                }
                grp.grades.Add(g);
            }

            var subjects = new List<JsonObject>();
            foreach (var (sid, grp) in groups)
            {
                summ.TryGetValue(sid, out var s);
                var sorted = grp.grades.OrderBy(x => x.Str("DateCreated", "Date"), StringComparer.Ordinal).ToList();
                var gradeArr = new JsonArray();
                foreach (var x in sorted) gradeArr.Add(CompactGrade(x));

                var proposed = s.Str("Entry_1");
                var final = s.Str("Entry_2");
                subjects.Add(new JsonObject
                {
                    ["name"] = grp.name,
                    ["position"] = grp.pos,
                    ["average"] = WeightedAvg(grp.grades) is double a ? a : null,
                    ["proposed"] = proposed.Length > 0 ? proposed : null,
                    ["final"] = final.Length > 0 ? final : null,
                    ["grades"] = gradeArr,
                });
            }

            var ordered = subjects
                .OrderBy(x => x.Int("position") ?? 999)
                .ThenBy(x => x.Str("name"), StringComparer.Ordinal)
                .ToList();
            var subjOut = new JsonArray();
            foreach (var x in ordered) subjOut.Add(x);

            outArr.Add(new JsonObject
            {
                ["periodId"] = b.PeriodId,
                ["number"] = b.Number,
                ["label"] = b.Label,
                ["current"] = b.Current,
                ["start"] = b.Start,
                ["end"] = b.End,
                ["overall"] = WeightedAvg(b.Grades) is double ov ? ov : null,
                ["subjects"] = subjOut,
            });
        }
        return outArr;
    }

    public static JsonArray BuildTimetable(JsonArray src)
    {
        var outArr = new JsonArray();
        foreach (var l in src)
        {
            if (l is null) continue;
            var visNode = J.Nav(l, "Visible");
            if (visNode != null && visNode.GetValueKind() == JsonValueKind.False) continue;
            var ch = J.Nav(l, "Change");
            int ctype = ch is JsonObject ? (ch.Int("Type") ?? 0) : 0;
            var subject = l.Str("Subject", "Name");
            outArr.Add(new JsonObject
            {
                ["date"] = l.Str("DateAt"),
                ["pos"] = l.Int("TimeSlot", "Position") is int pp ? pp : null,
                ["start"] = l.Str("TimeSlot", "Start"),
                ["end"] = l.Str("TimeSlot", "End"),
                ["subject"] = subject.Length > 0 ? subject : "?",
                ["room"] = l.Str("Room", "Code"),
                ["teacher"] = l.Str("TeacherPrimary", "DisplayName"),
                ["changed"] = ch is JsonObject,
                ["cancelled"] = ctype == 1,
                ["subst"] = ctype == 2,
            });
        }
        return outArr;
    }

    public static JsonObject BuildAttendance(JsonArray attendance)
    {
        var buckets = new Dictionary<string, int>
        { ["present"] = 0, ["late"] = 0, ["absent_exc"] = 0, ["absent_unexc"] = 0, ["neutral"] = 0, ["other"] = 0 };
        var bySubject = new Dictionary<string, int[]>();
        var log = new List<JsonObject>();

        foreach (var r in attendance)
        {
            if (r is null) continue;
            var cat = AttCat(J.Nav(r, "PresenceType"));
            buckets[cat] = buckets.GetValueOrDefault(cat) + 1;
            var subj = r.Str("Subject", "Name");
            if (subj.Length == 0) subj = "?";
            if (!bySubject.TryGetValue(subj, out var bs)) { bs = new int[4]; bySubject[subj] = bs; }
            int idx = cat switch { "present" => 0, "late" => 1, "absent_exc" => 2, "absent_unexc" => 3, _ => -1 };
            if (idx >= 0) bs[idx]++;
            log.Add(new JsonObject
            {
                ["date"] = r.Str("DayAt"),
                ["pos"] = r.Int("TimeSlot", "Position") is int pp ? pp : null,
                ["subject"] = subj,
                ["topic"] = r.Str("Topic"),
                ["cat"] = cat,
                ["name"] = r.Str("PresenceType", "Name"),
                ["teacher"] = r.Str("TeacherPrimary", "DisplayName"),
            });
        }

        log = log.OrderByDescending(x => x.Str("date"), StringComparer.Ordinal)
                 .ThenByDescending(x => x.Int("pos") ?? 0).ToList();
        var logArr = new JsonArray();
        foreach (var x in log) logArr.Add(x);

        int counted = buckets["present"] + buckets["late"] + buckets["absent_exc"] + buckets["absent_unexc"];
        int attended = buckets["present"] + buckets["late"];
        double? freq = counted > 0 ? Math.Round((double)attended / counted * 100, 1) : null;

        var subjAtt = new List<JsonObject>();
        foreach (var (subj, bs) in bySubject)
        {
            int c = bs[0] + bs[1] + bs[2] + bs[3];
            int a = bs[0] + bs[1];
            subjAtt.Add(new JsonObject
            {
                ["subject"] = subj,
                ["present"] = bs[0],
                ["late"] = bs[1],
                ["absent_exc"] = bs[2],
                ["absent_unexc"] = bs[3],
                ["total"] = c,
                ["percent"] = c > 0 ? Math.Round((double)a / c * 100, 1) : null,
            });
        }
        subjAtt = subjAtt.OrderBy(x => x.Num("percent") ?? 101)
                         .ThenBy(x => x.Str("subject"), StringComparer.Ordinal).ToList();
        var subjAttArr = new JsonArray();
        foreach (var x in subjAtt) subjAttArr.Add(x);

        var bucketsObj = new JsonObject();
        foreach (var (k, v) in buckets) bucketsObj[k] = v;

        return new JsonObject
        {
            ["buckets"] = bucketsObj,
            ["frequency"] = freq is double fr ? fr : null,
            ["counted"] = counted,
            ["bySubject"] = subjAttArr,
            ["log"] = logArr,
        };
    }

    public static JsonArray BuildExams(JsonArray exams)
    {
        var list = new List<JsonObject>();
        foreach (var e in exams)
        {
            if (e is null) continue;
            var subj = e.Str("Subject", "Name");
            var type = e.Str("Type");
            list.Add(new JsonObject
            {
                ["date"] = e.Str("DeadlineAt"),
                ["dateDisp"] = e.Str("Deadline", "DateDisplay"),
                ["subject"] = subj.Length > 0 ? subj : "?",
                ["type"] = type.Length > 0 ? type : "Sprawdzian",
                ["content"] = e.Str("Content"),
                ["teacher"] = e.Str("Creator", "DisplayName"),
            });
        }
        list = list.OrderBy(x => x.Str("date"), StringComparer.Ordinal).ToList();
        var arr = new JsonArray();
        foreach (var x in list) arr.Add(x);
        return arr;
    }

    public static JsonArray BuildHomework(JsonArray homework)
    {
        var list = new List<JsonObject>();
        foreach (var h in homework)
        {
            if (h is null) continue;
            var subj = h.Str("Subject", "Name");
            list.Add(new JsonObject
            {
                ["deadline"] = h.Str("DeadlineAt"),
                ["deadlineDisp"] = h.Str("Deadline", "DateDisplay"),
                ["assigned"] = h.Str("DateAt"),
                ["subject"] = subj.Length > 0 ? subj : "?",
                ["content"] = h.Str("Content"),
                ["teacher"] = h.Str("Creator", "DisplayName"),
                ["attachments"] = J.Nav(h, "Attachments").Arr().Count,
                ["answer"] = h.Bool("IsAnswerRequired"),
            });
        }
        list = list.OrderBy(x => x.Str("deadline"), StringComparer.Ordinal).ToList();
        var arr = new JsonArray();
        foreach (var x in list) arr.Add(x);
        return arr;
    }

    public static JsonArray BuildNotes(JsonArray notes)
    {
        var list = new List<JsonObject>();
        foreach (var n in notes)
        {
            if (n is null) continue;
            var date = n.Str("ValidAt");
            if (date.Length == 0) date = n.Str("DateValid", "Date");
            list.Add(new JsonObject
            {
                ["date"] = date,
                ["dateDisp"] = n.Str("DateValid", "DateDisplay"),
                ["positive"] = n.Bool("Positive"),
                ["category"] = n.Str("Category", "Name"),
                ["content"] = n.Str("Content"),
                ["teacher"] = n.Str("Creator", "DisplayName"),
                ["points"] = n.Num("Points") is double pp ? pp : null,
            });
        }
        list = list.OrderByDescending(x => x.Str("date"), StringComparer.Ordinal).ToList();
        var arr = new JsonArray();
        foreach (var x in list) arr.Add(x);
        return arr;
    }

    // ---- full view-model ----
    public static JsonObject Build(
        JsonObject pupil, JsonArray periods, double? lucky, List<PeriodBundle> bundles,
        JsonArray timetableSrc, JsonArray attendance, JsonArray exams, JsonArray homework,
        JsonArray notes, JsonObject status)
    {
        var student = BuildStudent(pupil);
        var grades = BuildGrades(bundles);

        var periodsOut = new JsonArray();
        foreach (var g in grades)
            periodsOut.Add(new JsonObject
            {
                ["periodId"] = g.Int("periodId") ?? 0,
                ["number"] = g.Int("number") ?? 0,
                ["label"] = g.Str("label"),
                ["current"] = g.Bool("current"),
                ["start"] = g.Str("start"),
                ["end"] = g.Str("end"),
            });

        bool premium = false;
        foreach (var kv in status)
        {
            var val = kv.Value.Str();
            if ((kv.Key.StartsWith("messages") || kv.Key == "addressbook") && val.Contains("PREMIUM"))
                premium = true;
        }

        return new JsonObject
        {
            ["meta"] = new JsonObject
            {
                ["generated"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["schoolYear"] = new JsonObject { ["start"] = student.Str("yearStart"), ["end"] = student.Str("yearEnd") },
                ["premiumLocked"] = premium,
                ["status"] = J.Clone(status),
            },
            ["student"] = student,
            ["lucky"] = lucky.HasValue && lucky.Value > 0 ? lucky.Value : null,
            ["periods"] = periodsOut,
            ["grades"] = grades,
            ["timetable"] = BuildTimetable(timetableSrc),
            ["attendance"] = BuildAttendance(attendance),
            ["exams"] = BuildExams(exams),
            ["homework"] = BuildHomework(homework),
            ["notes"] = BuildNotes(notes),
        };
    }
}
