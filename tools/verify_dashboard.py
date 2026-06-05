"""
Headless verification of dashboard.html — opens it in Chromium, captures any
console errors / uncaught exceptions, clicks through every view, and saves
screenshots to .verify/. Exit code 1 if any JS error is detected.

Usage:  py -3.12 tools/verify_dashboard.py
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent.parent
DASH = ROOT / "dashboard.html"
OUT = ROOT / ".verify"
OUT.mkdir(exist_ok=True)

VIEWS = ["overview", "grades", "attend", "plan", "exams", "hw", "notes", "msg"]


def main():
    if not DASH.exists():
        sys.exit("dashboard.html nie istnieje — uruchom export.py")

    errors = []
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        page = browser.new_page(viewport={"width": 1366, "height": 900})
        page.on("console", lambda m: errors.append(f"console.{m.type}: {m.text}") if m.type == "error" else None)
        page.on("pageerror", lambda e: errors.append(f"pageerror: {e}"))

        page.goto(DASH.as_uri())
        page.wait_for_timeout(600)

        # screenshot every view
        for vid in VIEWS:
            page.eval_on_selector(f"#view-{vid}", "el => el && el.scrollIntoView()")
            page.evaluate(f"setView('{vid}')")
            page.wait_for_timeout(350)
            page.screenshot(path=str(OUT / f"{vid}.png"), full_page=True)

        # interactions: grade period tabs, exam tabs, timetable nav, search
        page.evaluate("setView('grades')")
        tabs = page.query_selector_all("#view-grades .tabs button")
        for tb in tabs:
            tb.click(); page.wait_for_timeout(150)

        page.evaluate("setView('exams')")
        for tb in page.query_selector_all("#view-exams .tabs button"):
            tb.click(); page.wait_for_timeout(150)

        page.evaluate("setView('plan')")
        for btn in page.query_selector_all("#view-plan .tt-head .nav-btn"):
            btn.click(); page.wait_for_timeout(120)

        page.evaluate("setView('attend')")
        s = page.query_selector("#view-attend .search")
        if s:
            s.fill("matematyka"); page.wait_for_timeout(200)

        # theme toggle → Midnight, capture dark screenshots
        page.evaluate("setView('overview')")
        page.click("#theme-btn"); page.wait_for_timeout(500)
        page.screenshot(path=str(OUT / "overview_dark.png"), full_page=True)
        page.evaluate("setView('grades')"); page.wait_for_timeout(300)
        page.screenshot(path=str(OUT / "grades_dark.png"), full_page=True)
        page.evaluate("setView('plan')"); page.wait_for_timeout(300)
        page.screenshot(path=str(OUT / "plan_dark.png"), full_page=True)

        page.wait_for_timeout(200)
        browser.close()

    print(f"Zrzuty ekranu: {OUT}")
    if errors:
        print(f"\n❌ Wykryto {len(errors)} błędów JS:")
        for e in errors:
            print("   -", e)
        sys.exit(1)
    print("\n✅ Brak błędów JS — dashboard działa czysto we wszystkich widokach.")


if __name__ == "__main__":
    main()
