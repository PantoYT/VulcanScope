"""
Request signing for the Hebe mobile API.

Every call is authenticated by an RSA signature over a canonical string
(canonicalUrl [+ digest] + date). This is a faithful Python port of the
scheme used by the mobile app and by hebece — identical to the proven
implementation in the Vred bot, kept standalone here.
"""
from __future__ import annotations

import base64
import hashlib
import json
import re
from email.utils import formatdate
from urllib.parse import quote

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

# --- Constants matching the Android mobile app exactly ---
USER_AGENT       = "Dart/3.3 (dart:io)"
OPERATING_SYSTEM = "Android"
VERSION_CODE     = "621"
VAPI             = "1"
DEVICE_MODEL     = "Xiaomi MI 9"
APP_VERSION      = "25.02.14 (G)"
HOST             = "lekcjaplus.vulcan.net.pl"


def canonical_url(url: str) -> str:
    """Extract `api/mobile/...` (incl. query string), percent-encode, lowercase."""
    match = re.search(r"(api/mobile/.+)", url)
    if not match:
        raise ValueError(f"URL does not contain an api/mobile/ path: {url}")
    return quote(match.group(1), safe="").lower()


def _digest(body_str: str | None) -> str:
    if body_str is None:
        return ""
    return base64.b64encode(hashlib.sha256(body_str.encode()).digest()).decode()


def _load_private_key(private_key_b64: str):
    pem = (
        "-----BEGIN PRIVATE KEY-----\n"
        + "\n".join(private_key_b64[i:i + 64] for i in range(0, len(private_key_b64), 64))
        + "\n-----END PRIVATE KEY-----"
    )
    return serialization.load_pem_private_key(pem.encode(), password=None)


def _sign(fingerprint: str, private_key_b64: str, body_str: str | None, url: str, date_utc: str) -> dict:
    canonical = canonical_url(url)
    digest = _digest(body_str)

    sign_headers = ["vCanonicalUrl"]
    sign_values = canonical

    if body_str is not None:
        sign_headers.append("Digest")
        sign_values += digest

    sign_headers.append("vDate")
    sign_values += date_utc

    private_key = _load_private_key(private_key_b64)
    sig_bytes = private_key.sign(sign_values.encode(), padding.PKCS1v15(), hashes.SHA256())
    sig_b64 = base64.b64encode(sig_bytes).decode()

    return {
        "digest": f"SHA-256={digest}",
        "canonicalUrl": canonical,
        "signature": (
            f'keyId="{fingerprint}",headers="{" ".join(sign_headers)}",'
            f"algorithm=\"sha256withrsa\",signature=Base64(sha256withrsa({sig_b64}))"
        ),
    }


def build_headers(fingerprint: str, private_key_b64: str, body: dict | None, url: str) -> dict:
    """Build the full signed header set for a request to `url`."""
    date_utc = formatdate(usegmt=True)
    body_str = json.dumps(body, separators=(",", ":")) if body is not None else None
    sig = _sign(fingerprint, private_key_b64, body_str, url, date_utc)

    headers = {
        "accept":          "*/*",
        "accept-charset":  "UTF-8",
        "accept-encoding": "gzip",
        "connection":      "Keep-Alive",
        "content-type":    "application/json",
        "host":            HOST,
        "user-agent":      USER_AGENT,
        "vapi":            VAPI,
        "vdate":           date_utc,
        "vdevicemodel":    DEVICE_MODEL,
        "vos":             OPERATING_SYSTEM,
        "vversioncode":    VERSION_CODE,
        "signature":       sig["signature"],
        "vcanonicalurl":   sig["canonicalUrl"],
    }
    # Digest header only present when there is a body (GET requests omit it).
    if sig["digest"] != "SHA-256=":
        headers["digest"] = sig["digest"]
    return headers
