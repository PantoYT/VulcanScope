"""
VulcanClient — read-only access to the full Hebe journal.

Endpoints confirmed against hebece + the Vred bot. Period-bound resources
(grades, schedule) are fetched per semester so a single export covers the
whole school year; date-bound resources use the journal year range.
"""
from __future__ import annotations

import json
from datetime import date, datetime
from urllib.parse import urlencode

import requests

from .signing import build_headers


class VulcanApiError(RuntimeError):
    def __init__(self, code, message, url):
        self.code = code
        self.message = message
        self.url = url
        super().__init__(f"Hebe API error {code}: {message}  ({url})")


def _fmt(d) -> str:
    if isinstance(d, (datetime, date)):
        return d.strftime("%Y-%m-%d")
    return str(d)


class VulcanClient:
    def __init__(self, credentials_path: str = "credentials.json"):
        with open(credentials_path, encoding="utf-8") as f:
            creds = json.load(f)
        self._fingerprint = creds["fingerprint"]
        self._private_key = creds["privateKey"]
        self._rest_url = creds["restUrl"].rstrip("/")
        self.pupil = creds["pupil"]
        self._session = requests.Session()

    # ------------------------------------------------------------------ #
    # Identity helpers
    # ------------------------------------------------------------------ #
    @property
    def symbol(self) -> str:
        return self.pupil["Unit"]["Symbol"]

    @property
    def unit_id(self) -> int:
        return self.pupil["Unit"]["Id"]

    @property
    def pupil_id(self) -> int:
        return self.pupil["Pupil"]["Id"]

    @property
    def constituent_id(self) -> int:
        return self.pupil.get("ConstituentUnit", {}).get("Id", 0)

    @property
    def message_box(self) -> str:
        return self.pupil.get("MessageBox", {}).get("GlobalKey", "")

    @property
    def periods(self) -> list[dict]:
        return self.pupil.get("Periods", [])

    def current_period(self) -> dict:
        for p in self.periods:
            if p.get("Current"):
                return p
        return self.periods[-1]

    def year_range(self) -> tuple[str, str]:
        journal = self.pupil.get("Journal", {})
        first, last = self.periods[0], self.periods[-1]
        start = journal.get("StartAt") or first.get("StartAt") or first["Start"]["Date"]
        end = journal.get("EndAt") or last.get("EndAt") or last["End"]["Date"]
        return start, end

    # ------------------------------------------------------------------ #
    # Core request
    # ------------------------------------------------------------------ #
    def _get(self, path: str, params: dict | None = None):
        url = f"{self._rest_url}/{self.symbol}/api/mobile/{path}"
        if params:
            url += "?" + urlencode(params)
        headers = build_headers(self._fingerprint, self._private_key, None, url)
        r = self._session.get(url, headers=headers, timeout=30)
        r.raise_for_status()
        data = r.json()
        status = data.get("Status", {})
        if status.get("Code", 0) != 0:
            raise VulcanApiError(status.get("Code"), status.get("Message"), url)
        return data.get("Envelope")

    # ------------------------------------------------------------------ #
    # Resources
    # ------------------------------------------------------------------ #
    def get_lucky_number(self):
        env = self._get("school/lucky", {
            "pupilId": self.pupil_id,
            "constituentId": self.constituent_id,
            "day": _fmt(datetime.now()),
        })
        if isinstance(env, dict):
            return env.get("Number")
        return env

    def get_grades(self, period_id: int):
        return self._get("grade/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "periodId": period_id,
            "pageSize": 500,
        })

    def get_grades_summary(self, period_id: int):
        return self._get("grade/summary/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "periodId": period_id,
            "pageSize": 500,
        })

    def get_schedule(self, date_from, date_to, period_id: int):
        return self._get("schedule/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "periodId": period_id,
            "dateFrom": _fmt(date_from),
            "dateTo": _fmt(date_to),
            "pageSize": 500,
        })

    def get_schedule_changes(self, date_from, date_to, period_id: int):
        return self._get("schedule/withchanges/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "periodId": period_id,
            "dateFrom": _fmt(date_from),
            "dateTo": _fmt(date_to),
            "pageSize": 500,
        })

    def get_attendance(self, date_from, date_to):
        # lesson/byPupil returns realised lessons WITH attendance records.
        return self._get("lesson/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "dateFrom": _fmt(date_from),
            "dateTo": _fmt(date_to),
            "pageSize": 1000,
        })

    def get_exams(self, date_from, date_to):
        return self._get("exam/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "dateFrom": _fmt(date_from),
            "dateTo": _fmt(date_to),
            "pageSize": 500,
        })

    def get_homework(self, date_from, date_to):
        return self._get("homework/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "dateFrom": _fmt(date_from),
            "dateTo": _fmt(date_to),
            "pageSize": 500,
        })

    def get_notes(self):
        return self._get("note/byPupil", {
            "unitId": self.unit_id,
            "pupilId": self.pupil_id,
            "pageSize": 500,
        })

    def get_messages(self, kind: str = "received"):
        # kind: received | sent | deleted
        return self._get(f"messages/{kind}/byBox", {
            "box": self.message_box,
            "lastId": -2147483648,
            "pupilId": self.pupil_id,
            "pageSize": 500,
        })

    def get_addressbook(self):
        return self._get("addressbook", {"box": self.message_box})
