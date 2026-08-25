"""embeddings ueber die scads.ai gateway, gleicher api key wie ueberall sonst im projekt
kein lokales modell, siehe plan fuer die begruendung
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from pathlib import Path

API_URL = "https://llm.scads.ai/v1/embeddings"
MODEL = "Qwen/Qwen3-Embedding-4B"
API_KEY_FILENAME = ".scadsai-api-key"


def _api_key() -> str:
    key_path = Path.home() / API_KEY_FILENAME
    if not key_path.is_file():
        raise FileNotFoundError(f"kein api key gefunden unter {key_path}")
    return key_path.read_text(encoding="utf-8").strip()


def embed_texts(texts: list[str]) -> list[list[float]]:
    """ein embedding pro text, gleiche reihenfolge wie die eingabe, ein einziger request"""
    if not texts:
        return []

    headers = {
        "Authorization": f"Bearer {_api_key()}",
        "Content-Type": "application/json",
    }
    payload = json.dumps({"model": MODEL, "input": texts}).encode("utf-8")
    req = urllib.request.Request(API_URL, data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            data = json.loads(resp.read())
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"embedding request fehlgeschlagen: {error.code} {error.read().decode(errors='replace')}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"embedding gateway nicht erreichbar: {error.reason}") from error

    entries = sorted(data["data"], key=lambda entry: entry["index"])
    return [entry["embedding"] for entry in entries]
