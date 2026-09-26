# VulcanScope 🌋

[![build](https://github.com/PantoYT/VulcanScope/actions/workflows/build.yml/badge.svg)](https://github.com/PantoYT/VulcanScope/actions/workflows/build.yml)

A full export of the **eduVulcan / VULCAN (Hebe API)** school gradebook (used by Polish
schools) + a standalone, "cool" dashboard for browsing everything offline.

It downloads grades, attendance, the timetable, tests, homework, notes and
achievements — and generates a single `dashboard.html` you open in a browser (the data
is embedded in it; it works without internet and without a server).

> The project uses the same login mechanism as the **Vred** bot — RSA-key-signed
> requests to the "DzienniczekPlus 3.0" mobile API. It builds on the reverse
> engineering in [hebece](https://github.com/hypedevss/hebece).

---

## How login works (in short)

It isn't a password login on every request — it's **device pairing**:

1. **`register.py`** (once) — opens a browser and you log in manually at eduvulcan.pl.
   The script takes the JWT tokens from `/api/ap`, generates an **RSA 2048** key pair +
   a self-signed X.509 certificate and registers the public key on the account
   (`/api/mobile/register/jwt`). It saves `credentials.json`.
2. **`hebe/`** — from then on **the RSA private key = the login**. Every request is
   signed (`canonicalUrl + digest + data` → RSA-PKCS#1v1.5-SHA256), and the headers
   mimic the Android app. No password, no session, no expiry.

`credentials.json` = full read access to the gradebook without a password → **treat it
like a password** (it's in `.gitignore`).

---

## What gets exported

| Resource | Hebe endpoint | Example from an export |
|---|---|---|
| Partial grades (both semesters) | `grade/byPupil` | 225 grades |
| Predicted / final grades | `grade/summary/byPupil` | 49 entries |
| Attendance + lesson topics | `lesson/byPupil` | 1119 lessons, ~98% |
| Timetable + changes/substitutions | `schedule/withchanges/byPupil` | 1269 lessons |
| Tests and quizzes | `exam/byPupil` | 79 |
| Homework | `homework/byPupil` | 3 |
| Notes and achievements | `note/byPupil` | 2 |
| Lucky number | `school/lucky` | — |
| Messages + address book | `messages/*/byBox`, `addressbook` | 🔒 requires eduVulcan **Premium** |

Everything lands in `data/*.json` (raw data) + a compact `data/dashboard_data.json`,
which is embedded into `dashboard.html`.

> **About messages:** the API returns `EDUVULCAN_PREMIUM` for the mailbox and the address
> book, because the school's account has no active subscription. The export still
> succeeds — those two sections are simply skipped (the dashboard shows that clearly).

---

## Usage

```powershell
# 1. (once) dependencies
py -3.12 -m pip install -r requirements.txt
py -3.12 -m playwright install chromium   # only for register.py / verification

# 2. (once) log in and pair the device  →  creates credentials.json
py -3.12 register.py
#    ...or copy an existing credentials.json from the Vred bot

# 3. download the data and generate the dashboard
py -3.12 export.py

# 4. open dashboard.html in a browser  (or just run.bat)
```

`run.bat` does steps 3 + 4 in one click.

### Useful modes

```powershell
py -3.12 export.py --render-only      # rebuild the dashboard from the last data (no API)
py -3.12 tools/verify_dashboard.py    # headless test: checks for JS errors + screenshots
```

---

## Dashboard

One self-contained HTML file in a **glassmorphism** style (aurora gradient, SVG charts
without any CDN, animated bars/donut/line chart):

- **Overview** — weighted average, attendance, tiles, recent grades, averages chart, upcoming tests/homework
- **Grades** — semester switch, average per subject, colored grade "pills" (hover = weight, category, teacher, comment)
- **Attendance** — donut, statistics, breakdown per subject, a searchable log of lesson topics
- **Timetable** — weekly grid with navigation, substitutions (yellow) and cancellations (red)
- **Tests / Homework** — grouped by date, "in N days" badges
- **Notes** — positive/negative cards
- **Theme** — **Aurora (light)** / **Midnight (dark)** switch, remembered in `localStorage`
- **Animations** — counter count-up, "drawing" bars, donut and line chart (average over time)

The dashboard's interface is in Polish.

> The weighted average counts `+`/`-` as **+0.5 / −0.25** (the most common Polish
> convention — a constant in `export.py` and `csharp/`, easy to change). Point grades and
> `nb` are skipped.

---

## Timetable in Google Calendar

`ics_feed.py` is a small HTTP server that serves the timetable as an `.ics` feed for
subscription (Google Calendar → Other calendars → **From URL**).

```powershell
py -3.12 ics_feed.py            # listens on 127.0.0.1:8765
# or double-click ics_feed_launch.vbs (runs in the background, logs to ics_feed.log)
```

A deliberately simple architecture: it generates the ICS **live** on every request (no
cache, no scheduled job) and **needs no OAuth and no Google Cloud account** — it's a plain
URL subscription, not an API integration.

- **The cost of that simplicity**: Google refreshes a subscribed calendar by itself every
  ~8–24 h, with no way to speed it up — substitutions/cancellations can be up to a day
  stale. Deliberately accepted: it's a calendar for planning the day, not an alert
  system. A real push (OAuth + a scheduled job) was considered too, but it would cost
  another Google Cloud project.
- The token in the URL (`ics_token.txt`, gitignored) is the only protection — treat it
  like a password; anyone who knows the URL sees the timetable.
- The server has to be running for Google to poll it — keep it up (e.g. via
  `ics_feed_launch.vbs` at startup), otherwise the refresh simply won't happen that day
  and will be retried next time.
- **To do on your side (once)**: expose `127.0.0.1:8765` at a public address. The
  `kompu-tunnel` on this PC runs in remotely managed mode (token, no local
  `config.yml`), so the new rule (Public Hostname → `http://localhost:8765`) is added in
  the Cloudflare Zero Trust panel, not in a file. Then paste the full address with the
  token into Google Calendar.

---

## C# — CLI + terminal mode

A full port in **.NET 10** (`csharp/`) — the same RSA signing mechanism, the same data,
the same `dashboard.html`. Plus an interactive terminal dashboard (Spectre.Console) and
commands with `--json` output for embedding in a larger application.

```powershell
cd csharp
dotnet build
dotnet run -- register            # pair the account (without Python) — keygen + register/jwt
dotnet run -- tui                 # interactive terminal dashboard
dotnet run -- export              # data/*.json + dashboard.html (as in Python)
dotnet run -- grades -p 2         # semester 2 grades (table)
dotnet run -- attendance          # attendance + statistics
dotnet run -- plan                # timetable (next few days)
dotnet run -- exams --all         # tests
dotnet run -- lucky --json        # {"lucky": null}
```

**Account pairing in C# (`register`)** — without Python/Playwright: it generates an RSA
pair + certificate, logs you in in the browser, and after you paste the
`https://eduvulcan.pl/api/ap` page (or `--ap-file`) it performs `register/jwt` +
`register/hebe` and saves `credentials.json`. `register --selftest` verifies the
cryptography (signature roundtrip) and the `register/hebe` path.

### Standalone `.exe` (no .NET install needed)

```powershell
cd csharp
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist
# → dist/vulcanscope.exe (~36 MB) — a friend runs it without any SDK:
.\dist\vulcanscope.exe exams --json
```

Or **without building**: ready binaries (Windows/Linux/macOS) are built by GitHub Actions
and attached to every [Release](https://github.com/PantoYT/VulcanScope/releases) (tag
`v*`) — just download `vulcanscope-win-x64.exe`.

**Export as commands for a larger application:** every data command accepts `--json` and
prints clean JSON to stdout (exit codes: `0` ok, `2` API error, `3` missing file). A larger
application can call e.g. `vulcanscope grades --json` and parse the result — or use the
`VulcanClient` / `ViewModel` / `Exporter` classes directly as a library.

> `credentials.json` is shared with the Python part (auto-detected up the directory
> tree). Account registration can still go through `register.py` (Playwright).

---

## Structure

```
VulcanScope/
├── hebe/                 # Python client (signing.py, client.py)
├── export.py             # downloads everything → data/*.json + dashboard.html
├── register.py           # one-time account pairing (browser)
├── ics_feed.py           # .ics server — timetable in Google Calendar
├── ics_feed_launch.vbs   # runs ics_feed.py in the background
├── web/template.html     # dashboard template (Aurora/Midnight, data placeholder)
├── tools/verify_dashboard.py   # headless test (Playwright)
├── csharp/               # C# port (.NET 10)
│   ├── Program.cs
│   └── src/              # Signing, VulcanClient, ViewModel, Exporter, Tui, Cli …
├── run.bat
├── credentials.json      # 🔒 secret (gitignored)
├── data/                 # 🔒 exported data (gitignored)
└── dashboard.html        # 🔒 generated (gitignored)
```

For use with **your own** account only. The data stays locally on your computer.
