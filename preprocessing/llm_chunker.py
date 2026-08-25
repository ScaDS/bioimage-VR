"""rohe key_values (freie metadaten-annotationen, feldnamen variieren je quelle/studie)
per llm den FIELD_* kategorien aus rag_schema.py zuordnen, statt fester kandidaten-keys
pro quelle wie es idr_adapter.py vorher gemacht hat

ein call pro bild beim indexbau (offline, unkritisch fuer latenz), nie zur query-zeit -
macht adapter fuer neue quellen mit komplett anderen feldnamen quellenunabhaengig
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from pathlib import Path

from rag_schema import FIELD_DESCRIPTIONS, FIELD_RESOLUTION

API_URL = "https://llm.scads.ai/v1/chat/completions"
MODEL = "alias-ha"
API_KEY_FILENAME = ".scadsai-api-key"

# resolution kommt strukturiert aus den pixeldaten (siehe idr_adapter._resolution_text),
# key_values enthalten das so gut wie nie - dem llm gar nicht erst anbieten
KEY_VALUE_FIELD_TYPES = {k: v for k, v in FIELD_DESCRIPTIONS.items() if k != FIELD_RESOLUTION}

SYSTEM_PROMPT = (
    "Du bekommst rohe Metadaten-Felder (Feldname: Wert) aus einer Mikroskopie-Datenbank und "
    "ordnest jedes Feld einer passenden Kategorie zu. Pro Chunk: stelle dem Originalwert ein "
    "kurzes deutsches Label voran, gefolgt von einem Doppelpunkt, dann der Originalwert "
    "WORTWOERTLICH UNVERAENDERT - nicht uebersetzen, nicht umformulieren, nicht "
    "zusammenfassen, auch wenn der Wert englisch ist. Das Label beschreibt WAS INHALTLICH "
    "IM WERT STEHT, nicht den rohen Feldnamen aus den Metadaten - ein Feld namens 'Protocol 2' "
    "dessen Wert von einer Antikoerper-Faerbung handelt bekommt ein Label zur Faerbung, nicht "
    "'Protokoll 2:'. Das Label ist eine kurze Verb-Phrase wie Anfang einer Antwort auf eine "
    "typische Frage dazu (z.b. 'Verwendete Faerbung:', 'Verwendetes Mikroskop:', "
    "'Angewandte Segmentierungsmethode:', 'Beobachteter Phaenotyp:'), NIEMALS ein blosses "
    "Substantiv ohne Verb-Bezug (NICHT 'Faerbung:', NICHT 'Mikroskop:'). Ein Feld namens "
    "'Segmented Channel' mit Wert 'LaminB1' wird zu 'Segmentierter Kanal: LaminB1.' - NIEMALS "
    "nur den nackten Wert ohne Label ausgeben. "
    "JEDES Feld bleibt sein EIGENER Chunk, auch wenn mehrere Felder derselben Kategorie "
    "zugeordnet werden (z.b. drei Protokoll-Felder -> drei separate Chunks, NICHT "
    "zusammenfassen) - Ausnahme: zwei Felder die nur zusammen einen Sinn ergeben (z.b. "
    "Segmentierungsmethode + segmentierter Kanal) duerfen in einem Chunk stehen. Erfinde "
    "keine Fakten die nicht in den Feldern stehen. Felder ohne fachlichen Bezug zu einer "
    "Kategorie laesst du weg, keine Platzhalter fuer fehlende Kategorien. Antworte "
    "ausschliesslich als JSON-Liste von Objekten mit den Schluesseln 'field_type' und "
    "'text', kein Text drumherum."
)


def _api_key() -> str:
    key_path = Path.home() / API_KEY_FILENAME
    if not key_path.is_file():
        raise FileNotFoundError(f"kein api key gefunden unter {key_path}")
    return key_path.read_text(encoding="utf-8").strip()


def _extract_json_list(content: str) -> list:
    """manche modelle wickeln die antwort in markdown-codefences oder ein
    {"chunks": [...]} objekt statt direkt die liste zu liefern - beides abfangen"""
    text = content.strip()
    if text.startswith("```"):
        text = text.split("```")[1]
        if text.startswith("json"):
            text = text[4:]
        text = text.strip()
    parsed = json.loads(text)
    if isinstance(parsed, dict):
        parsed = next((v for v in parsed.values() if isinstance(v, list)), [])
    return parsed


def classify_key_values(key_values: dict) -> list[dict]:
    """key_values -> liste von {"field_type", "text"}, leere liste falls nichts passt
    oder key_values leer ist"""
    if not key_values:
        return []

    categories = "\n".join(f"- {name}: {desc}" for name, desc in KEY_VALUE_FIELD_TYPES.items())
    user_prompt = (
        f"Kategorien:\n{categories}\n\n"
        f"Rohe Metadaten-Felder:\n{json.dumps(key_values, ensure_ascii=False, indent=2)}"
    )
    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": user_prompt},
    ]
    headers = {
        "Authorization": f"Bearer {_api_key()}",
        "Content-Type": "application/json",
    }
    payload = json.dumps({"model": MODEL, "messages": messages}).encode("utf-8")
    req = urllib.request.Request(API_URL, data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=90) as resp:
            data = json.loads(resp.read())
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"klassifizierung fehlgeschlagen: {error.code} {error.read().decode(errors='replace')}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"gateway nicht erreichbar: {error.reason}") from error

    content = data["choices"][0]["message"]["content"] or "[]"
    try:
        parsed = _extract_json_list(content)
    except (json.JSONDecodeError, IndexError) as error:
        raise RuntimeError(f"klassifizierung: konnte antwort nicht als json lesen: {content[:300]!r}") from error

    valid = []
    for entry in parsed:
        field_type = entry.get("field_type")
        text = entry.get("text")
        if field_type in KEY_VALUE_FIELD_TYPES and text:
            valid.append({"field_type": field_type, "text": text})
    return valid
