"""
Serwer HTTP oddający plan lekcji jako feed .ics do subskrypcji w Google
Calendar (Inne kalendarze → + → Z adresu URL).

Świadomie prosty: generuje ICS NA ŻYWO przy każdym żądaniu (Google i tak
odpytuje raz na ~8-24h wg własnego harmonogramu, więc cache nie ma sensu) i
nie wymaga żadnego OAuth ani projektu Google Cloud — to zwykła subskrypcja
adresu URL. Dokładność "na już" nie jest celem; celem jest mieć plan
w kalendarzu bez ręcznego eksportu.

Użycie:
    py -3.12 ics_feed.py [--port 8765]

Adres do wklejenia w Google Calendar to http://<host>/<token>.ics, gdzie
<host> to publiczny adres za tunelem (patrz ics_feed_launch.vbs), a token
jest generowany przy pierwszym uruchomieniu i zapisany w ics_token.txt
(gitignore — traktować jak sekret: kto zna URL, widzi plan lekcji).
"""
from __future__ import annotations

import argparse
import secrets
import sys
from datetime import date, datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hebe import VulcanClient  # noqa: E402

TOKEN_FILE = Path(__file__).resolve().parent / "ics_token.txt"
CHUNK_DAYS = 28


def get_token() -> str:
    if TOKEN_FILE.exists():
        return TOKEN_FILE.read_text(encoding="utf-8").strip()
    token = secrets.token_hex(16)
    TOKEN_FILE.write_text(token, encoding="utf-8")
    return token


def _period_bounds(p: dict) -> tuple[str, str]:
    start = p.get("StartAt") or p["Start"]["Date"]
    end = p.get("EndAt") or p["End"]["Date"]
    return start, end


def _last_sunday(year: int, month: int) -> date:
    d = date(year + 1, 1, 1) if month == 12 else date(year, month + 1, 1)
    d -= timedelta(days=1)
    while d.weekday() != 6:  # Sunday
        d -= timedelta(days=1)
    return d


def _warsaw_utc_offset_hours(d: date) -> int:
    """Reguła DST UE (bez zależności od pakietu tzdata, którego brak na tym Pythonie):
    czas letni od ostatniej niedzieli marca do ostatniej niedzieli października."""
    dst_start, dst_end = _last_sunday(d.year, 3), _last_sunday(d.year, 10)
    return 2 if dst_start <= d < dst_end else 1


def _dt_utc(date_str: str, time_str: str) -> str:
    naive = datetime.strptime(f"{date_str} {time_str}", "%Y-%m-%d %H:%M")
    utc = naive - timedelta(hours=_warsaw_utc_offset_hours(naive.date()))
    return utc.strftime("%Y%m%dT%H%M%SZ")


def _escape(text: str) -> str:
    return (text.replace("\\", "\\\\").replace(";", "\\;")
                .replace(",", "\\,").replace("\n", "\\n"))


def _fetch_all_lessons(vc: VulcanClient) -> list[dict]:
    lessons = []
    for p in vc.periods:
        start, end = _period_bounds(p)
        cursor, d_end = date.fromisoformat(start), date.fromisoformat(end)
        while cursor <= d_end:
            win_end = min(cursor + timedelta(days=CHUNK_DAYS - 1), d_end)
            lessons += vc.get_schedule_changes(cursor.isoformat(), win_end.isoformat(), p["Id"]) or []
            cursor = win_end + timedelta(days=1)
    return lessons


def build_ics(vc: VulcanClient) -> str:
    lines = [
        "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//VulcanScope//Plan lekcji//PL",
        "CALSCALE:GREGORIAN", "METHOD:PUBLISH", "X-WR-CALNAME:Plan lekcji",
    ]
    now_utc = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    seen: set = set()
    for l in _fetch_all_lessons(vc):
        if l.get("Visible") is False:
            continue
        lid = l.get("Id")
        if lid is None or lid in seen:
            continue
        seen.add(lid)
        d = l.get("DateAt", "")
        ts = l.get("TimeSlot") or {}
        t_start, t_end = ts.get("Start"), ts.get("End")
        if not (d and t_start and t_end):
            continue
        subject = (l.get("Subject") or {}).get("Name", "?")
        room = (l.get("Room") or {}).get("Code") if l.get("Room") else None
        teacher = (l.get("TeacherPrimary") or {}).get("DisplayName") if l.get("TeacherPrimary") else None
        ch = l.get("Change") or None
        ctype = ch.get("Type") if ch else 0
        prefix = "\u274c ODWOLANE: " if ctype == 1 else "\u26a0 ZASTEPSTWO: " if ctype == 2 else ""
        lines += [
            "BEGIN:VEVENT",
            f"UID:vulcanscope-{lid}@panto-dev.com",
            f"DTSTAMP:{now_utc}",
            f"DTSTART:{_dt_utc(d, t_start)}",
            f"DTEND:{_dt_utc(d, t_end)}",
            f"SUMMARY:{_escape(prefix + subject)}",
        ]
        if room:
            lines.append(f"LOCATION:{_escape(room)}")
        if teacher:
            lines.append(f"DESCRIPTION:{_escape(teacher)}")
        lines.append("END:VEVENT")
    lines.append("END:VCALENDAR")
    return "\r\n".join(lines) + "\r\n"


def make_handler(token: str):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path != f"/{token}.ics":
                self.send_response(404)
                self.end_headers()
                return
            try:
                body = build_ics(VulcanClient()).encode("utf-8")
            except Exception as e:
                print(f"[ics_feed] BLAD: {e}")
                self.send_response(502)
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/calendar; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, fmt, *args):
            print(f"[ics_feed] {self.log_date_time_string()} {self.address_string()} {fmt % args}")

    return Handler


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()
    token = get_token()
    print(f"[ics_feed] nasluchuje na 127.0.0.1:{args.port}")
    print(f"[ics_feed] feed: http://127.0.0.1:{args.port}/{token}.ics")
    HTTPServer(("127.0.0.1", args.port), make_handler(token)).serve_forever()


if __name__ == "__main__":
    main()
