"""
Odświeża zserializowanego "pupil" (Unit/Periods/Journal) w credentials.json.

Po co: `credentials.json` zapisuje stan ucznia RAZ, przy parowaniu (register.py).
Pola Periods/Journal nie odświeżają się same — na przełomie roku szkolnego
"Current" wskazuje dalej na okres, który już się skończył, więc plan lekcji na
dziś wychodzi pusty (żaden okres w cache nie pokrywa dzisiejszej daty), a eksport
ciągnie dane starego roku. Ten skrypt pobiera świeży stan przez register/hebe
(ten sam klucz RSA co zwykłe żądania — nie trzeba się logować ponownie) i
podmienia tylko pole "pupil", zostawiając fingerprint/klucz/certyfikat bez zmian.

Działa na DOWOLNYM credentials.json z tego samego formatu — także na tym
używanym przez bota Vred (DiscordBots/Vred/credentials.json), bo klucz do
podpisu żądań jest w pliku, a nie zaszyty w tym repo.

Użycie:
    py -3.12 refresh_pupil.py                          # ./credentials.json
    py -3.12 refresh_pupil.py --path ../DiscordBots/Vred/credentials.json
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

import requests

from hebe.signing import build_headers

# Windows terminals default to cp1250 — wymuś UTF-8, żeby polskie znaki nie wywalały skryptu.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")
    except Exception:
        pass


def refresh(path: Path) -> None:
    original_text = path.read_text(encoding="utf-8")
    creds = json.loads(original_text)

    fingerprint = creds["fingerprint"]
    private_key = creds["privateKey"]
    rest_url = creds["restUrl"].rstrip("/")
    old_pupil_id = creds["pupil"]["Pupil"]["Id"]

    url = f"{rest_url}/api/mobile/register/hebe?mode=2"
    headers = build_headers(fingerprint, private_key, None, url)
    r = requests.get(url, headers=headers, timeout=15)
    r.raise_for_status()
    data = r.json()
    if data.get("Status", {}).get("Code", 0) != 0:
        raise RuntimeError(f"register/hebe error: {data['Status']}")

    matches = [p for p in data["Envelope"] if p["Pupil"]["Id"] == old_pupil_id]
    if len(matches) != 1:
        raise RuntimeError(
            f"Oczekiwano dokładnie jednego ucznia o Id={old_pupil_id} w odpowiedzi, "
            f"znaleziono {len(matches)}. Konto ma więcej dzieci? Wybierz ręcznie."
        )

    old_journal = creds["pupil"].get("Journal")
    creds["pupil"] = matches[0]

    backup = path.with_suffix(path.suffix + ".bak")
    if not backup.exists():
        backup.write_text(original_text, encoding="utf-8")

    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(creds, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)

    new_journal = creds["pupil"].get("Journal")
    print(f"✅ Odświeżono {path}")
    print(f"   Rok szkolny: {old_journal} → {new_journal}")
    print(f"   Klasa: {creds['pupil'].get('ClassDisplay')}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    ap.add_argument("--path", default="credentials.json", help="Ścieżka do credentials.json")
    args = ap.parse_args()
    refresh(Path(args.path))


if __name__ == "__main__":
    main()
