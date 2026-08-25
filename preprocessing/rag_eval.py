"""rag pipeline end-to-end pruefen: pro lokal vorhandenem idr bild werden die harten,
eindeutig pruefbaren fakten (rag_ground_truth.py) als frage an den echten vlm-chat
gestellt - bild + frage + rag kontext, genau wie in der app (VLMClient.cs) - und die
antwort automatisch gegen die ground truth geprueft (einfacher substring vergleich,
keine bewertung durch ein llm - bleibt so nachvollziehbar/deterministisch)

lauf: python rag_eval.py
schreibt data/rag_eval_results.csv, eine zeile pro (bild, fakt). fuer die
auswertung/grafiken siehe rag_eval_report.py
"""

from __future__ import annotations

import csv
import json
import re
import time
import urllib.error
import urllib.request
from pathlib import Path

from fetch_from_idr import DATA_ROOT
from rag_ground_truth import FACT_RESOLUTION, QUESTIONS, extract_hard_facts
from rag_index import query as rag_query_index

SCADS_API_BASE = "https://llm.scads.ai/v1"
SCADS_API_KEY_FILENAME = ".scadsai-api-key"
VISION_MODEL = "alias-vision"
THUMBNAIL_URL = "https://idr.openmicroscopy.org/webclient/render_thumbnail/{id}/"

OUTPUT_CSV = DATA_ROOT.parent / "rag_eval_results.csv"
CSV_FIELDS = ["image_id", "fact_type", "question", "expected", "answer", "correct"]


def _api_key() -> str:
    key_path = Path.home() / SCADS_API_KEY_FILENAME
    if not key_path.is_file():
        raise FileNotFoundError(f"kein api key gefunden unter {key_path}")
    return key_path.read_text(encoding="utf-8").strip()


def _thumbnail_bytes(image_id: str) -> bytes:
    """einmal pro bild von idr geladen, danach lokal gecacht - unabhaengig vom
    3d volumen (nii.gz), reicht als 2d bild fuers vision modell"""
    cache_path = DATA_ROOT / image_id / "thumbnail.jpg"
    if cache_path.is_file():
        return cache_path.read_bytes()
    with urllib.request.urlopen(THUMBNAIL_URL.format(id=image_id), timeout=30) as resp:
        data = resp.read()
    cache_path.write_bytes(data)
    return data


def _rag_context(question: str, image_id: str) -> str:
    """gleiche logik wie upload_server.py's /rag_query endpunkt, hier direkt
    in-process statt ueber http - damit der eval nicht von einem laufenden
    server abhaengt"""
    own_hits = rag_query_index(question, n_results=4, source_id=image_id)
    other_hits = rag_query_index(question, n_results=4)
    other_hits = [h for h in other_hits if h["metadata"]["source_id"] != str(image_id)]

    parts = []
    if own_hits:
        parts.append("Aktuelles Bild:")
        parts.extend(f"- {h['text']}" for h in own_hits)
    if other_hits:
        parts.append("Vergleichbare Datensaetze:")
        parts.extend(f"- (Bild {h['metadata']['source_id']}) {h['text']}" for h in other_hits[:3])
    return "\n".join(parts)


def _ask_vlm(question: str, context: str, image_bytes: bytes) -> str:
    import base64

    data_url = f"data:image/jpeg;base64,{base64.b64encode(image_bytes).decode()}"
    messages = []
    if context:
        messages.append({"role": "system", "content": context})
    messages.append({"role": "user", "content": [
        {"type": "text", "text": question},
        {"type": "image_url", "image_url": data_url},
    ]})
    headers = {"Authorization": f"Bearer {_api_key()}", "Content-Type": "application/json"}
    payload = json.dumps({"model": VISION_MODEL, "messages": messages}).encode("utf-8")
    req = urllib.request.Request(f"{SCADS_API_BASE}/chat/completions", data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.loads(resp.read())
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"vlm anfrage fehlgeschlagen: {error.code} {error.read().decode(errors='replace')}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"vlm gateway nicht erreichbar: {error.reason}") from error
    return data["choices"][0]["message"]["content"] or ""


def _grade(fact_type: str, expected: str, answer: str) -> bool:
    """einfacher, nachvollziehbarer vergleich statt llm-als-richter. resolution ist
    ein sonderfall (drei zahlen, vlm formatiert die meist anders als "x, y, z" -
    jede zahl fuer sich pruefen statt den ganzen string).

    fuer alles andere: wortstamm-vergleich statt exaktem substring - das vlm
    antwortet auf deutsch und uebersetzt englische fachbegriffe aus den metadaten
    konsequent (z.b. "Manual" -> "Manuell"), ein exakter stringvergleich wuerde
    das faelschlich als falsch werten obwohl die antwort inhaltlich stimmt. jedes
    wort ab 4 zeichen im erwarteten wert muss (auf die ersten 4 zeichen gekuerzt)
    in der antwort vorkommen - kurze wortstaemme wie bei "manuell"/"manual"
    ueberleben eine deutsche endung, kein hartkodiertes uebersetzungs-woerterbuch"""
    answer_lower = answer.lower()
    if fact_type == FACT_RESOLUTION:
        numbers = re.findall(r"\d+\.\d{2}", expected)
        # vlm schreibt zahlen manchmal mit deutschem komma statt punkt
        return all(number in answer or number.replace(".", ",") in answer for number in numbers)

    stems = [word[:4].lower() for word in re.split(r"[^\w]+", expected) if len(word) >= 4]
    if not stems:
        return expected.lower() in answer_lower
    return all(stem in answer_lower for stem in stems)


def run(limit: int | None = None) -> None:
    rows = []
    image_dirs = sorted(d for d in DATA_ROOT.iterdir() if d.is_dir())
    if limit:
        image_dirs = image_dirs[:limit]

    for image_dir in image_dirs:
        metadata_path = image_dir / "metadata.json"
        if not metadata_path.is_file():
            continue
        raw = json.loads(metadata_path.read_text(encoding="utf-8"))
        image_id = str(raw["image_id"])
        facts = extract_hard_facts(raw)
        if not facts:
            continue

        try:
            image_bytes = _thumbnail_bytes(image_id)
        except (urllib.error.HTTPError, urllib.error.URLError) as error:
            print(f"[{image_id}] thumbnail fehlgeschlagen, uebersprungen: {error}")
            continue

        for fact_type, expected in facts.items():
            question = QUESTIONS[fact_type]
            try:
                context = _rag_context(question, image_id)
                answer = _ask_vlm(question, context, image_bytes)
            except RuntimeError as error:
                print(f"[{image_id}/{fact_type}] fehlgeschlagen: {error}")
                continue

            correct = _grade(fact_type, expected, answer)
            rows.append({
                "image_id": image_id,
                "fact_type": fact_type,
                "question": question,
                "expected": expected,
                "answer": answer,
                "correct": correct,
            })
            mark = "OK" if correct else "FEHLER"
            print(f"[{image_id}/{fact_type}] {mark}  erwartet={expected!r}  antwort={answer[:80]!r}")
            time.sleep(0.2)

    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS)
        writer.writeheader()
        writer.writerows(rows)

    correct_count = sum(1 for r in rows if r["correct"])
    print(f"\nfertig: {len(rows)} fragen, {correct_count} richtig ({correct_count / len(rows):.0%}) -> {OUTPUT_CSV}")


if __name__ == "__main__":
    run()
