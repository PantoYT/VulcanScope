"""
VulcanScope — full journal export.

Pulls everything the Hebe API will give us for the registered pupil and
writes it to data/*.json (one file per resource + a combined all.json).
If web/template.html exists, also renders a self-contained dashboard.html.

Usage:  py -3.12 export.py
"""
from __future__ import annotations

import json
import sys
import traceback
from datetime import datetime, timedelta
from pathlib import Path

from hebe import VulcanClient

# Windows terminals default to cp1250 — force UTF-8 so Polish output never crashes.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent
DATA_DIR = ROOT / "data"
CREDS = ROOT / "credentials.json"
TEMPLATE = ROOT / "web" / "template.html"
DASHBOARD = ROOT / "dashboard.html"

CHUNK_DAYS = 28


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #
def _d(s: str) -> datetime:
    return datetime.strptime(s, "%Y-%m-%d")


def period_bounds(p: dict) -> tuple[str, str]:
    """Nowo utworzony bieżący okres ma tylko płaskie StartAt/EndAt — bez
    zagnieżdżonego Start/End, które API dopisuje później."""
    start = p.get("StartAt") or p["Start"]["Date"]
    end = p.get("EndAt") or p["End"]["Date"]
    return start, end


def windows(start: str, end: str, days: int = CHUNK_DAYS):
    """Yield non-overlapping (from, to) date strings covering [start, end]."""
    cur = _d(start)
    last = _d(end)
    while cur <= last:
        win_end = min(cur + timedelta(days=days - 1), last)
        yield cur.strftime("%Y-%m-%d"), win_end.strftime("%Y-%m-%d")
        cur = win_end + timedelta(days=1)


def dedup(items: list, key: str = "Id") -> list:
    seen = set()
    out = []
    for it in items:
        k = it.get(key) if isinstance(it, dict) else None
        if k is None:
            out.append(it)
        elif k not in seen:
            seen.add(k)
            out.append(it)
    return out


class Reporter:
    def __init__(self):
        self.status: dict[str, str] = {}
        self.counts: dict[str, int] = {}

    def run(self, name: str, fn, count_of=None):
        try:
            result = fn()
            self.status[name] = "ok"
            if count_of is not None:
                self.counts[name] = count_of(result)
            elif isinstance(result, list):
                self.counts[name] = len(result)
            print(f"  [ok]   {name:<18} {self.counts.get(name, '')}")
            return result
        except Exception as e:  # noqa: BLE001 — intentional: partial failures are fine
            self.status[name] = f"error: {e}"
            print(f"  [FAIL] {name:<18} {e}")
            return None


# --------------------------------------------------------------------------- #
# Distilled student profile
# --------------------------------------------------------------------------- #
def build_student(pupil: dict) -> dict:
    p = pupil.get("Pupil", {})
    unit = pupil.get("Unit", {})
    homeroom = []
    for ed in pupil.get("Educators", []):
        for role in ed.get("Roles", []):
            if "ychowawca" in (role.get("RoleName") or ""):
                homeroom.append(f"{ed.get('Name','')} {ed.get('Surname','')}".strip())
    return {
        "firstName": p.get("FirstName", ""),
        "secondName": p.get("SecondName", ""),
        "surname": p.get("Surname", ""),
        "fullName": f"{p.get('FirstName','')} {p.get('Surname','')}".strip(),
        "class": pupil.get("ClassDisplay", ""),
        "info": pupil.get("InfoDisplay", ""),
        "school": unit.get("DisplayName") or unit.get("Name", ""),
        "schoolShort": unit.get("Short", ""),
        "address": unit.get("Address", ""),
        "symbol": unit.get("Symbol", ""),
        "homeroom": sorted(set(homeroom)),
        "capabilities": pupil.get("Capabilities", []),
        "yearStart": pupil.get("Journal", {}).get("StartAt", ""),
        "yearEnd": pupil.get("Journal", {}).get("EndAt", ""),
        "pupilNumber": pupil.get("Journal", {}).get("PupilNumber"),
    }


def period_label(p: dict) -> str:
    return f"Semestr {p.get('Number', '?')}"


# --------------------------------------------------------------------------- #
# View model — compact structure consumed by the dashboard
# --------------------------------------------------------------------------- #
PLUS_MOD = 0.5    # "+" grade modifier (most common Polish convention)
MINUS_MOD = -0.25  # "-" grade modifier


def _num(v):
    return v if isinstance(v, (int, float)) else None


def _grade_points(g):
    """Return (value, weight) for a 1-6 grade, applying +/- modifiers, else (None, None)."""
    v = _num(g.get("Value"))
    if v is None or v <= 0 or v > 6:
        return None, None
    w = _num((g.get("Column") or {}).get("Weight")) or 0
    if w <= 0:
        return None, None
    content = (g.get("Content") or "").strip()
    val = float(v)
    if content.endswith("+"):
        val += PLUS_MOD
    elif content.endswith("-"):
        val += MINUS_MOD
    return val, w


def weighted_avg(grades) -> float | None:
    num = den = 0.0
    for g in grades:
        val, w = _grade_points(g)
        if val is None:
            continue
        num += val * w
        den += w
    return round(num / den, 2) if den else None


def _compact_grade(g):
    col = g.get("Column") or {}
    return {
        "content": g.get("Content", ""),
        "value": _num(g.get("Value")),
        "weight": col.get("Weight") or 0,
        "name": col.get("Name", ""),
        "category": (col.get("Category") or {}).get("Name", ""),
        "date": (g.get("DateCreated") or {}).get("Date", ""),
        "dateDisp": (g.get("DateCreated") or {}).get("DateDisplay", ""),
        "teacher": (g.get("Creator") or {}).get("DisplayName", ""),
        "comment": g.get("Comment", "") or "",
    }


def _attendance_cat(pt):
    if not pt:
        return "other"
    flags = (pt.get("Presence"), pt.get("Absence"), pt.get("Late"),
             pt.get("AbsenceJustified"), pt.get("LegalAbsence"))
    if all(x is None for x in flags):
        return "other"
    if pt.get("Late"):
        return "late"
    if pt.get("Absence"):
        return "absent_exc" if (pt.get("AbsenceJustified") or pt.get("LegalAbsence")) else "absent_unexc"
    if pt.get("Presence"):
        return "present"
    return "neutral"  # e.g. "lekcja się nie odbyła", "nie uczęszcza na religię"


def build_view_model(student, periods, lucky, grades_by_period, timetable_src,
                     attendance, exams, homework, notes, status) -> dict:
    # --- grades per period, grouped by subject ---
    grades_vm = []
    for per in grades_by_period:
        summ = {s.get("Subject", {}).get("Id"): s for s in (per["summary"] or [])}
        subjects: dict = {}
        for g in per["grades"] or []:
            subj = (g.get("Column") or {}).get("Subject") or {}
            sid = subj.get("Id")
            d = subjects.setdefault(sid, {"name": subj.get("Name", "?"),
                                          "position": subj.get("Position", 999),
                                          "grades": []})
            d["grades"].append(g)
        subj_list = []
        for sid, d in subjects.items():
            s = summ.get(sid) or {}
            ordered = sorted(d["grades"], key=lambda x: (x.get("DateCreated") or {}).get("Date", ""))
            subj_list.append({
                "name": d["name"],
                "position": d["position"],
                "average": weighted_avg(d["grades"]),
                "proposed": s.get("Entry_1"),
                "final": s.get("Entry_2"),
                "grades": [_compact_grade(x) for x in ordered],
            })
        subj_list.sort(key=lambda x: (x["position"], x["name"]))
        grades_vm.append({
            "periodId": per["periodId"], "number": per["number"], "label": per["label"],
            "current": per["current"], "start": per["start"], "end": per["end"],
            "overall": weighted_avg(per["grades"] or []),
            "subjects": subj_list,
        })

    # --- timetable (compact, visible lessons) ---
    timetable = []
    for l in timetable_src or []:
        if l.get("Visible") is False:
            continue
        ch = l.get("Change") or None
        ctype = ch.get("Type") if ch else 0
        ts = l.get("TimeSlot") or {}
        timetable.append({
            "date": l.get("DateAt", ""),
            "pos": ts.get("Position"),
            "start": ts.get("Start", ""),
            "end": ts.get("End", ""),
            "subject": (l.get("Subject") or {}).get("Name", "?"),
            "room": (l.get("Room") or {}).get("Code", "") if l.get("Room") else "",
            "teacher": (l.get("TeacherPrimary") or {}).get("DisplayName", "") if l.get("TeacherPrimary") else "",
            "changed": bool(ch),
            "cancelled": ctype == 1,
            "subst": ctype == 2,
        })

    # --- attendance ---
    buckets = {"present": 0, "late": 0, "absent_exc": 0, "absent_unexc": 0, "neutral": 0, "other": 0}
    by_subject: dict = {}
    att_log = []
    for r in attendance or []:
        cat = _attendance_cat(r.get("PresenceType"))
        buckets[cat] = buckets.get(cat, 0) + 1
        subj = (r.get("Subject") or {}).get("Name", "?")
        bs = by_subject.setdefault(subj, {"present": 0, "late": 0, "absent_exc": 0, "absent_unexc": 0})
        if cat in bs:
            bs[cat] += 1
        att_log.append({
            "date": r.get("DayAt", ""),
            "pos": (r.get("TimeSlot") or {}).get("Position"),
            "subject": subj,
            "topic": r.get("Topic", "") or "",
            "cat": cat,
            "name": (r.get("PresenceType") or {}).get("Name", ""),
            "teacher": (r.get("TeacherPrimary") or {}).get("DisplayName", ""),
        })
    att_log.sort(key=lambda x: (x["date"], x["pos"] or 0), reverse=True)
    counted = buckets["present"] + buckets["late"] + buckets["absent_exc"] + buckets["absent_unexc"]
    attended = buckets["present"] + buckets["late"]
    freq = round(attended / counted * 100, 1) if counted else None
    subj_att = []
    for subj, bs in by_subject.items():
        c = bs["present"] + bs["late"] + bs["absent_exc"] + bs["absent_unexc"]
        a = bs["present"] + bs["late"]
        subj_att.append({**bs, "subject": subj, "total": c,
                         "percent": round(a / c * 100, 1) if c else None})
    subj_att.sort(key=lambda x: (x["percent"] if x["percent"] is not None else 101, x["subject"]))

    attendance_vm = {
        "buckets": buckets,
        "frequency": freq,
        "counted": counted,
        "bySubject": subj_att,
        "log": att_log,
    }

    # --- exams / homework / notes ---
    exams_vm = sorted([{
        "date": e.get("DeadlineAt", ""),
        "dateDisp": (e.get("Deadline") or {}).get("DateDisplay", ""),
        "subject": (e.get("Subject") or {}).get("Name", "?"),
        "type": e.get("Type", "") or "Sprawdzian",
        "content": e.get("Content", "") or "",
        "teacher": (e.get("Creator") or {}).get("DisplayName", ""),
    } for e in exams or []], key=lambda x: x["date"])

    homework_vm = sorted([{
        "deadline": h.get("DeadlineAt", ""),
        "deadlineDisp": (h.get("Deadline") or {}).get("DateDisplay", ""),
        "assigned": h.get("DateAt", ""),
        "subject": (h.get("Subject") or {}).get("Name", "?"),
        "content": h.get("Content", "") or "",
        "teacher": (h.get("Creator") or {}).get("DisplayName", ""),
        "attachments": len(h.get("Attachments") or []),
        "answer": bool(h.get("IsAnswerRequired")),
    } for h in homework or []], key=lambda x: x["deadline"])

    notes_vm = sorted([{
        "date": n.get("ValidAt", "") or (n.get("DateValid") or {}).get("Date", ""),
        "dateDisp": (n.get("DateValid") or {}).get("DateDisplay", ""),
        "positive": bool(n.get("Positive")),
        "category": (n.get("Category") or {}).get("Name", ""),
        "content": n.get("Content", "") or "",
        "teacher": (n.get("Creator") or {}).get("DisplayName", ""),
        "points": n.get("Points"),
    } for n in notes or []], key=lambda x: x["date"], reverse=True)

    premium_locked = any("PREMIUM" in str(v) for k, v in status.items()
                         if k.startswith("messages") or k == "addressbook")

    return {
        "meta": {
            "generated": datetime.now().isoformat(timespec="seconds"),
            "schoolYear": {"start": student["yearStart"], "end": student["yearEnd"]},
            "premiumLocked": premium_locked,
            "status": status,
        },
        "student": student,
        "lucky": lucky if (lucky and lucky > 0) else None,
        "periods": [{"periodId": p["periodId"], "number": p["number"], "label": p["label"],
                     "current": p["current"], "start": p["start"], "end": p["end"]} for p in grades_vm],
        "grades": grades_vm,
        "timetable": timetable,
        "attendance": attendance_vm,
        "exams": exams_vm,
        "homework": homework_vm,
        "notes": notes_vm,
    }


# --------------------------------------------------------------------------- #
# Main
# --------------------------------------------------------------------------- #
def main():
    if not CREDS.exists():
        sys.exit(f"Brak {CREDS} — skopiuj credentials.json z zarejestrowanego konta.")

    print(f"=== VulcanScope export === ({datetime.now():%Y-%m-%d %H:%M:%S})\n")
    client = VulcanClient(str(CREDS))
    rep = Reporter()

    student = build_student(client.pupil)
    print(f"Uczeń: {student['fullName']}  |  klasa {student['class']}  |  {student['schoolShort']}")
    year_start, year_end = client.year_range()
    print(f"Rok szkolny: {year_start} → {year_end}\n")
    print("Pobieram dane:")

    # --- lucky number ------------------------------------------------------ #
    lucky = rep.run("lucky", client.get_lucky_number, count_of=lambda x: 1 if x else 0)

    # --- grades + summary, per period ------------------------------------- #
    grades_by_period = []
    for p in client.periods:
        pid = p["Id"]
        grades = rep.run(f"grades[{p.get('Number')}]", lambda pid=pid: client.get_grades(pid)) or []
        summary = rep.run(f"summary[{p.get('Number')}]", lambda pid=pid: client.get_grades_summary(pid)) or []
        grades_by_period.append({
            "periodId": pid,
            "number": p.get("Number"),
            "level": p.get("Level"),
            "label": period_label(p),
            "current": bool(p.get("Current")),
            "start": period_bounds(p)[0],
            "end": period_bounds(p)[1],
            "grades": grades,
            "summary": summary,
        })

    # --- schedule + changes (per period, chunked) ------------------------- #
    def fetch_schedule():
        out = []
        for p in client.periods:
            for f, t in windows(*period_bounds(p)):
                out += client.get_schedule(f, t, p["Id"]) or []
        return dedup(out)

    def fetch_changes():
        out = []
        for p in client.periods:
            for f, t in windows(*period_bounds(p)):
                out += client.get_schedule_changes(f, t, p["Id"]) or []
        return dedup(out)

    schedule = rep.run("schedule", fetch_schedule) or []
    changes = rep.run("scheduleChanges", fetch_changes) or []

    # --- attendance (chunked, no period) ---------------------------------- #
    def fetch_attendance():
        out = []
        for f, t in windows(year_start, year_end):
            out += client.get_attendance(f, t) or []
        return dedup(out)

    attendance = rep.run("attendance", fetch_attendance) or []

    # --- exams / homework (sparse, full range) ---------------------------- #
    exams = rep.run("exams", lambda: client.get_exams(year_start, year_end)) or []
    homework = rep.run("homework", lambda: client.get_homework(year_start, year_end)) or []

    # --- notes ------------------------------------------------------------ #
    notes = rep.run("notes", client.get_notes) or []

    # --- messages --------------------------------------------------------- #
    messages = {}
    for kind in ("received", "sent", "deleted"):
        messages[kind] = rep.run(f"messages[{kind}]", lambda kind=kind: client.get_messages(kind)) or []

    # --- addressbook ------------------------------------------------------ #
    addressbook = rep.run("addressbook", client.get_addressbook) or []

    # --- assemble --------------------------------------------------------- #
    data = {
        "meta": {
            "generated": datetime.now().isoformat(timespec="seconds"),
            "generator": "VulcanScope",
            "schoolYear": {"start": year_start, "end": year_end},
            "status": rep.status,
        },
        "student": student,
        "periods": client.periods,
        "luckyNumber": lucky,
        "grades": grades_by_period,
        "schedule": schedule,
        "scheduleChanges": changes,
        "attendance": attendance,
        "exams": exams,
        "homework": homework,
        "notes": notes,
        "messages": messages,
        "addressbook": addressbook,
        "pupilRaw": client.pupil,
    }

    # --- compact view model for the dashboard ----------------------------- #
    vm = build_view_model(
        student, client.periods, lucky, grades_by_period,
        changes or schedule, attendance, exams, homework, notes, rep.status,
    )

    # --- write files ------------------------------------------------------ #
    DATA_DIR.mkdir(exist_ok=True)
    sections = {
        "dashboard_data": vm,
        "student": student,
        "grades": grades_by_period,
        "schedule": schedule,
        "scheduleChanges": changes,
        "attendance": attendance,
        "exams": exams,
        "homework": homework,
        "notes": notes,
        "messages": messages,
        "addressbook": addressbook,
        "lucky": lucky,
        "all": data,
    }
    for name, payload in sections.items():
        (DATA_DIR / f"{name}.json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )

    print(f"\nZapisano {len(sections)} plików JSON do {DATA_DIR}")

    # --- render dashboard ------------------------------------------------- #
    if TEMPLATE.exists():
        html = TEMPLATE.read_text(encoding="utf-8")
        payload = json.dumps(vm, ensure_ascii=False).replace("</", "<\\/")
        html = html.replace("/*__DATA__*/null/*__DATA__*/", payload)
        DASHBOARD.write_text(html, encoding="utf-8")
        size_kb = DASHBOARD.stat().st_size / 1024
        print(f"Wygenerowano dashboard.html ({size_kb:.0f} KB) — otwórz w przeglądarce.")
    else:
        print("(web/template.html nie istnieje — pomijam dashboard; JSON gotowy)")

    # --- summary ---------------------------------------------------------- #
    print("\nPodsumowanie:")
    for name, st in rep.status.items():
        mark = "✓" if st == "ok" else "✗"
        cnt = rep.counts.get(name, "")
        print(f"  {mark} {name:<18} {cnt}  {'' if st=='ok' else st}")


def render_only():
    """Re-render dashboard.html from the last export — no API calls."""
    vm_path = DATA_DIR / "dashboard_data.json"
    if not vm_path.exists():
        sys.exit("Brak data/dashboard_data.json — najpierw pełny eksport: py -3.12 export.py")
    if not TEMPLATE.exists():
        sys.exit("Brak web/template.html")
    vm = json.loads(vm_path.read_text(encoding="utf-8"))
    html = TEMPLATE.read_text(encoding="utf-8")
    payload = json.dumps(vm, ensure_ascii=False).replace("</", "<\\/")
    html = html.replace("/*__DATA__*/null/*__DATA__*/", payload)
    DASHBOARD.write_text(html, encoding="utf-8")
    print(f"Re-render: dashboard.html ({DASHBOARD.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    try:
        if "--render-only" in sys.argv:
            render_only()
        else:
            main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
