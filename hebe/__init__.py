"""
VulcanScope — Hebe API client for eduvulcan.pl / Dziennik VULCAN.

Pure-read client built on the same request-signing scheme the official
"DzienniczekPlus 3.0" mobile app uses (reverse engineered, RSA-signed
HTTP requests). Pairing/registration is reused from the Vred bot — this
package only *reads* an already-registered identity (credentials.json).
"""
from .client import VulcanClient, VulcanApiError

__all__ = ["VulcanClient", "VulcanApiError"]
