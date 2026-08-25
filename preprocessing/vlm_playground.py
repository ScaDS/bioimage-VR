"""lokales test-tool: verschiedene vision-language-modelle (mehrere anbieter) auf
demselben screenshot mit verschiedenen system-/user-prompts vergleichen, inkl.
tts-vorlesen der antwort und csv-mitschrieb aller laeufe.

komplett unabhaengig von der unity-vr-app und vom rest des projekts - eine einzige
datei ohne eigene imports aus preprocessing/*, kann also auch alleine z.b. per mail/
slack verschickt werden und funktioniert dann genauso. laeuft lokal per FastAPI wie
upload_server.py (eigener port, damit beide gleichzeitig laufen koennen).

benoetigt (bei der empfaengerin): python 3.10+ und
    pip install fastapi uvicorn python-multipart
sowie einen eigenen ~/.scadsai-api-key fuer den ScaDS.AI anbieter (der einzige der
ohne weiteres setup laeuft) - ohne eigenen key bleiben nur Ollama/Gemini/OpenAI
uebrig, die wiederum eigene installation/keys brauchen.

vier anbieter, gemischt aus "laeuft sofort" und "eigener api key noetig":
  - ScaDS.AI      kein key noetig (nutzt ~/.scadsai-api-key wie der rest vom projekt),
                  alle dort verfuegbaren modelle werden live aufgelistet
  - Ollama        kostenlos, laeuft komplett lokal - braucht aber eine eigene Ollama-
                  Installation (https://ollama.com) mit einem gezogenen vision-modell
                  (z.b. "ollama pull llava" oder "qwen2.5vl"). wird automatisch erkannt,
                  ohne installation zeigt das dropdown einfach nichts an
  - Google Gemini eigener api key noetig (kostenloser tier reicht, key von
                  https://aistudio.google.com/apikey), wird nirgends gespeichert nur
                  pro anfrage mitgeschickt
  - OpenAI        eigener api key noetig, nicht kostenlos - gleiches prinzip

usage:
    python vlm_playground.py
    (zeigt lokale wlan-adresse, z.b. http://192.168.1.23:8010)

screenshots vorher in vlm_playground_data/samples/ (liegt direkt neben dieser datei,
wird beim ersten start automatisch angelegt) legen - tauchen automatisch im dropdown
auf - oder direkt im formular hochladen. jeder lauf wird an
vlm_playground_data/log.csv angehaengt (timestamp, anbieter, modell, bild, beide
prompts, antwort, dauer - NIE der api key). keine hugging-face-anbindung (braucht
account/token, absichtlich nicht blind verdrahtet).

kein auth-schutz - fuers vertraute lokale wlan gedacht, nicht oeffentliches internet.
"""

from __future__ import annotations

import base64
import csv
import json
import socket
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import HTMLResponse, JSONResponse, Response

SCADS_API_BASE = "https://llm.scads.ai/v1"
SCADS_API_KEY_FILENAME = ".scadsai-api-key"
OLLAMA_BASE = "http://localhost:11434"
GEMINI_BASE = "https://generativelanguage.googleapis.com/v1beta"
OPENAI_BASE = "https://api.openai.com/v1"
IDR_THUMBNAIL_URL = "https://idr.openmicroscopy.org/webclient/render_thumbnail/{id}/"

# rag ist absichtlich optional - diese datei soll auch alleine bei jemandem ohne den
# rest vom projekt (kein chromadb, kein data/rag_index/) funktionieren, nur eben ohne
# den rag vergleichs-abschnitt. siehe RAG_TESTING.md fuers eigentliche rag setup
try:
    from rag_index import query as rag_query_chunks
    RAG_AVAILABLE = True
except Exception:
    RAG_AVAILABLE = False

# keine oeffentliche "welche modelle koennen bilder" liste bei gemini/openai ohne
# eigenen api-call (der wieder einen key braucht) - kuratierte, bekanntermassen
# vision-faehige modelle reichen fuers vergleichs-tool
GEMINI_MODELS = ["gemini-2.0-flash", "gemini-1.5-flash", "gemini-1.5-pro"]
OPENAI_MODELS = ["gpt-4o", "gpt-4o-mini"]

PROVIDERS = {
    "scads": {"label": "ScaDS.AI", "needs_key": False},
    "ollama": {"label": "Ollama (lokal)", "needs_key": False},
    "gemini": {"label": "Google Gemini", "needs_key": True},
    "openai": {"label": "OpenAI", "needs_key": True},
}

# bewusst NICHT von der projekt-ordnerstruktur abhaengig (kein "../data") - diese datei
# soll auch alleine an jemanden verschickt werden koennen und dann ueberall funktionieren,
# alles landet einfach direkt neben der datei selbst
DATA_ROOT = Path(__file__).resolve().parent / "vlm_playground_data"
SAMPLES_DIR = DATA_ROOT / "samples"
LOG_PATH = DATA_ROOT / "log.csv"
LOG_FIELDS = ["timestamp", "provider", "model", "image", "system_prompt", "user_prompt", "rag_used", "response", "latency_ms"]

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}
MIME_BY_EXT = {
    ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
    ".webp": "image/webp", ".bmp": "image/bmp",
}

app = FastAPI()


def _http_json(url: str, payload: Optional[dict], headers: dict, timeout: int) -> dict:
    """gemeinsamer http helfer, urllib statt requests (keine neue abhaengigkeit)"""
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, headers=headers, method="POST" if data else "GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{error.code}: {error.read().decode(errors='replace')}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"Nicht erreichbar: {error.reason}") from error


# ---------------------------------------------------------------- ScaDS.AI ----

def _scads_api_key() -> str:
    key_path = Path.home() / SCADS_API_KEY_FILENAME
    if not key_path.is_file():
        raise FileNotFoundError(f"Kein API-Key gefunden unter {key_path}")
    return key_path.read_text(encoding="utf-8").strip()


def scads_list_models() -> list[str]:
    headers = {"Authorization": f"Bearer {_scads_api_key()}"}
    data = _http_json(f"{SCADS_API_BASE}/models", None, headers, timeout=15)
    return [m["id"] for m in data.get("data", [])]


def scads_ask(model: str, system_prompt: str, user_prompt: str, image_bytes: bytes, mime: str) -> str:
    # image_url als reiner data-url-STRING, nicht das offizielle {"url": ...} objekt -
    # dieses gateway erwartet es so (siehe VLMClient.cs), empirisch verifiziert
    data_url = f"data:{mime};base64,{base64.b64encode(image_bytes).decode()}"
    messages = []
    if system_prompt.strip():
        messages.append({"role": "system", "content": system_prompt})
    messages.append({"role": "user", "content": [
        {"type": "text", "text": user_prompt},
        {"type": "image_url", "image_url": data_url},
    ]})
    headers = {"Authorization": f"Bearer {_scads_api_key()}", "Content-Type": "application/json"}
    data = _http_json(f"{SCADS_API_BASE}/chat/completions", {"model": model, "messages": messages}, headers, timeout=120)
    return data["choices"][0]["message"]["content"] or "(keine Antwort)"


def scads_speak(text: str, voice: str) -> bytes:
    headers = {"Authorization": f"Bearer {_scads_api_key()}", "Content-Type": "application/json"}
    req = urllib.request.Request(f"{SCADS_API_BASE}/audio/speech",
        data=json.dumps({"model": "tts-1-hd", "input": text, "voice": voice}).encode("utf-8"),
        headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=60) as resp:
        return resp.read()


# ------------------------------------------------------------------ Ollama ----
# lokale, kostenlose modelle - eigene, gut dokumentierte api (nicht die openai-
# kompatible schicht, um von ollama-versionsunterschieden dabei unabhaengig zu sein)

def ollama_list_models() -> list[str]:
    try:
        data = _http_json(f"{OLLAMA_BASE}/api/tags", None, {}, timeout=3)
    except Exception:
        return []  # nicht installiert/nicht gestartet - dropdown bleibt einfach leer
    return [m["name"] for m in data.get("models", [])]


def ollama_ask(model: str, system_prompt: str, user_prompt: str, image_bytes: bytes, mime: str) -> str:
    del mime  # ollama braucht kein mime, nur die rohen base64 bytes
    messages = []
    if system_prompt.strip():
        messages.append({"role": "system", "content": system_prompt})
    messages.append({
        "role": "user",
        "content": user_prompt,
        "images": [base64.b64encode(image_bytes).decode()],
    })
    payload = {"model": model, "messages": messages, "stream": False}
    data = _http_json(f"{OLLAMA_BASE}/api/chat", payload, {"Content-Type": "application/json"}, timeout=180)
    return data.get("message", {}).get("content") or "(keine Antwort)"


# -------------------------------------------------------------- Google Gemini -

def gemini_ask(model: str, system_prompt: str, user_prompt: str, image_bytes: bytes, mime: str, api_key: str) -> str:
    if not api_key:
        raise ValueError("Gemini braucht einen eigenen API-Key (siehe Hinweis im Formular).")
    payload = {
        "contents": [{"parts": [
            {"text": user_prompt},
            {"inline_data": {"mime_type": mime, "data": base64.b64encode(image_bytes).decode()}},
        ]}],
    }
    if system_prompt.strip():
        payload["system_instruction"] = {"parts": [{"text": system_prompt}]}
    url = f"{GEMINI_BASE}/models/{model}:generateContent?key={api_key}"
    data = _http_json(url, payload, {"Content-Type": "application/json"}, timeout=120)
    try:
        return data["candidates"][0]["content"]["parts"][0]["text"] or "(keine Antwort)"
    except (KeyError, IndexError) as error:
        raise RuntimeError(f"Unerwartetes Antwortformat: {data}") from error


# -------------------------------------------------------------------- OpenAI --

def openai_ask(model: str, system_prompt: str, user_prompt: str, image_bytes: bytes, mime: str, api_key: str) -> str:
    if not api_key:
        raise ValueError("OpenAI braucht einen eigenen API-Key (siehe Hinweis im Formular).")
    data_url = f"data:{mime};base64,{base64.b64encode(image_bytes).decode()}"
    messages = []
    if system_prompt.strip():
        messages.append({"role": "system", "content": system_prompt})
    messages.append({"role": "user", "content": [
        {"type": "text", "text": user_prompt},
        {"type": "image_url", "image_url": {"url": data_url}},  # offizielles objekt-format
    ]})
    headers = {"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"}
    data = _http_json(f"{OPENAI_BASE}/chat/completions", {"model": model, "messages": messages}, headers, timeout=120)
    return data["choices"][0]["message"]["content"] or "(keine Antwort)"


# ---------------------------------------------------------------- gemeinsam ---

def ask_provider(provider: str, model: str, system_prompt: str, user_prompt: str,
                  image_bytes: bytes, mime: str, api_key: str) -> str:
    if provider == "scads":
        return scads_ask(model, system_prompt, user_prompt, image_bytes, mime)
    if provider == "ollama":
        return ollama_ask(model, system_prompt, user_prompt, image_bytes, mime)
    if provider == "gemini":
        return gemini_ask(model, system_prompt, user_prompt, image_bytes, mime, api_key)
    if provider == "openai":
        return openai_ask(model, system_prompt, user_prompt, image_bytes, mime, api_key)
    raise ValueError(f"Unbekannter Anbieter: {provider}")


def _mime_for(filename: str) -> str:
    return MIME_BY_EXT.get(Path(filename).suffix.lower(), "image/png")


# -------------------------------------------------------------------- RAG -----
# vergleich "mit vs ohne rag kontext" fuers gleiche modell/frage/bild, plus die
# rohen chunks sichtbar (nicht nur den fertigen text) - damit man dem ergebnis bis
# zum ursprung nachgehen kann statt dem fertigen kontext blind zu vertrauen

def fetch_idr_thumbnail(image_id: int) -> bytes:
    """oeffentliches idr thumbnail, kein login noetig - gleiche url die auch
    upload_server.py im browser direkt als <img src> nutzt, hier serverseitig
    geholt weil wir die rohen bytes fuers vlm brauchen, nicht nur zum anzeigen"""
    url = IDR_THUMBNAIL_URL.format(id=image_id)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return resp.read()


def build_rag_context(image_id: int, question: str) -> dict:
    """gleiche logik wie upload_server.py's /rag_query - eigene chunks (gefiltert
    auf image_id) plus aehnliche chunks aus anderen bildern (ungefiltert), damit
    aehnlichkeit ueber den gesamten index automatisch mit auftaucht"""
    own_hits = rag_query_chunks(question, n_results=4, source_id=image_id)
    other_hits = rag_query_chunks(question, n_results=4)
    other_hits = [h for h in other_hits if h["metadata"]["source_id"] != str(image_id)][:3]

    parts = []
    if own_hits:
        parts.append("Aktuelles Bild:")
        parts.extend(f"- {h['text']}" for h in own_hits)
    if other_hits:
        parts.append("Vergleichbare Datensaetze:")
        parts.extend(f"- (Bild {h['metadata']['source_id']}) {h['text']}" for h in other_hits)

    return {"context": "\n".join(parts), "own_hits": own_hits, "other_hits": other_hits}


def _log_row(row: dict) -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    is_new = not LOG_PATH.is_file()
    with LOG_PATH.open("a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=LOG_FIELDS)
        if is_new:
            writer.writeheader()
        writer.writerow(row)


def _read_log(limit: int = 40) -> list[dict]:
    if not LOG_PATH.is_file():
        return []
    with LOG_PATH.open(newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    return list(reversed(rows))[:limit]


@app.get("/", response_class=HTMLResponse)
def index() -> str:
    return PAGE


@app.get("/providers")
def providers() -> JSONResponse:
    return JSONResponse({"providers": PROVIDERS})


@app.get("/models")
def models(provider: str = "scads") -> JSONResponse:
    try:
        if provider == "scads":
            return JSONResponse({"models": scads_list_models()})
        if provider == "ollama":
            found = ollama_list_models()
            note = None if found else "Ollama nicht erreichbar (installiert? gestartet? 'ollama pull llava' gemacht?)"
            return JSONResponse({"models": found, "note": note})
        if provider == "gemini":
            return JSONResponse({"models": GEMINI_MODELS})
        if provider == "openai":
            return JSONResponse({"models": OPENAI_MODELS})
        return JSONResponse({"models": [], "error": f"Unbekannter Anbieter: {provider}"}, status_code=400)
    except Exception as error:
        return JSONResponse({"models": [], "error": str(error)}, status_code=502)


@app.get("/samples")
def samples() -> JSONResponse:
    SAMPLES_DIR.mkdir(parents=True, exist_ok=True)
    files = sorted(p.name for p in SAMPLES_DIR.iterdir() if p.suffix.lower() in IMAGE_EXTENSIONS)
    return JSONResponse({"samples": files})


@app.get("/samples/{name}")
def sample_file(name: str) -> Response:
    path = SAMPLES_DIR / name
    if not path.is_file() or path.parent != SAMPLES_DIR:
        return Response(status_code=404)
    return Response(content=path.read_bytes(), media_type=_mime_for(name))


@app.get("/history")
def history() -> JSONResponse:
    return JSONResponse({"rows": _read_log()})


@app.get("/rag_available")
def rag_available() -> JSONResponse:
    return JSONResponse({"available": RAG_AVAILABLE})


@app.get("/idr_rag_context")
def idr_rag_context(image_id: int, question: str) -> JSONResponse:
    if not RAG_AVAILABLE:
        return JSONResponse({"error": "kein rag index verfuegbar (siehe RAG_TESTING.md)"}, status_code=400)
    try:
        return JSONResponse(build_rag_context(image_id, question))
    except Exception as error:
        return JSONResponse({"error": str(error)}, status_code=502)


@app.post("/ask")
def ask(
    provider: str = Form(...),
    model: str = Form(...),
    system_prompt: str = Form(""),
    user_prompt: str = Form(...),
    sample_name: str = Form(""),
    idr_image_id: str = Form(""),
    rag_context: str = Form(""),
    api_key: str = Form(""),
    file: UploadFile = File(None),
) -> JSONResponse:
    if file is not None and file.filename:
        image_bytes = file.file.read()
        image_name = file.filename
        mime = file.content_type or _mime_for(file.filename)
    elif sample_name:
        path = SAMPLES_DIR / sample_name
        if not path.is_file():
            return JSONResponse({"error": f"Sample '{sample_name}' nicht gefunden."}, status_code=400)
        image_bytes = path.read_bytes()
        image_name = sample_name
        mime = _mime_for(sample_name)
    elif idr_image_id:
        try:
            image_bytes = fetch_idr_thumbnail(int(idr_image_id))
        except Exception as error:
            return JSONResponse({"error": f"IDR Thumbnail nicht ladbar: {error}"}, status_code=502)
        image_name = f"idr:{idr_image_id}"
        mime = "image/jpeg"
    else:
        return JSONResponse({"error": "Kein Bild ausgewaehlt (Upload, Sample oder IDR-ID)."}, status_code=400)

    # rag kontext als teil des system prompts, gleiches prinzip wie die eigene
    # system message in VLMClient.cs - hier reicht ein string zusammenfuegen,
    # kein provider unterscheidet dafuer extra rollen
    combined_system_prompt = f"{rag_context}\n\n{system_prompt}".strip() if rag_context.strip() else system_prompt

    started = time.monotonic()
    try:
        answer = ask_provider(provider, model, combined_system_prompt, user_prompt, image_bytes, mime, api_key)
    except Exception as error:
        return JSONResponse({"error": str(error)}, status_code=502)
    latency_ms = round((time.monotonic() - started) * 1000)

    # api_key bewusst NICHT mitgeloggt
    _log_row({
        "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "provider": provider,
        "model": model,
        "image": image_name,
        "system_prompt": combined_system_prompt,
        "user_prompt": user_prompt,
        "rag_used": bool(rag_context.strip()),
        "response": answer,
        "latency_ms": latency_ms,
    })

    return JSONResponse({"response": answer, "latency_ms": latency_ms, "image": image_name})


@app.post("/speak")
def speak(text: str = Form(...), voice: str = Form("alloy")) -> Response:
    try:
        audio = scads_speak(text, voice)
    except Exception as error:
        return JSONResponse({"error": str(error)}, status_code=502)
    return Response(content=audio, media_type="audio/mpeg")


def _local_ip() -> str:
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        s.close()


PAGE = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>VLM Playground</title>
<style>
  :root {
    --bg: #12141a; --surface: #ffffff0f; --surface-hover: #ffffff1f; --border: #ffffff26;
    --accent: #4dccbd; --accent-soft: #4dccbd2e; --violet: #a88ff2; --warning: #f28c59;
    --text: #ffffff; --text-dim: #ffffffa6;
  }
  * { box-sizing: border-box; }
  body {
    font-family: -apple-system, "Segoe UI", system-ui, sans-serif;
    background: radial-gradient(circle at 20% 0%, #1b2530 0%, var(--bg) 55%);
    color: var(--text); min-height: 100vh; margin: 0; padding: 32px 16px;
  }
  main { max-width: 760px; margin: 0 auto; }
  h1 {
    font-size: 1.4rem; margin: 0 0 4px;
    background: linear-gradient(90deg, var(--accent), var(--violet));
    -webkit-background-clip: text; background-clip: text; color: transparent; display: inline-block;
  }
  .subtitle { color: var(--text-dim); font-size: 0.9rem; margin: 0 0 24px; line-height: 1.4; }
  .subtitle code { color: var(--text); }
  .card { background: var(--surface); border: 1px solid var(--border); border-radius: 16px; padding: 20px; margin-bottom: 20px; }
  .card h2 { font-size: 0.8rem; margin: 0 0 14px; color: var(--text-dim); font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; }
  .tabs { display: flex; gap: 8px; margin-bottom: 12px; }
  .tab { flex: 1; padding: 8px; text-align: center; border-radius: 8px; background: var(--surface); cursor: pointer; color: var(--text-dim); font-size: 0.85rem; }
  .tab.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  select, textarea, input[type=text], input[type=password] {
    width: 100%; padding: 10px 12px; background: var(--surface); border: 1px solid var(--border);
    border-radius: 10px; color: var(--text); font-size: 0.9rem; font-family: inherit;
  }
  select:focus, textarea:focus, input:focus { outline: none; border-color: var(--accent); }
  textarea { resize: vertical; min-height: 56px; }
  label { display: block; font-size: 0.82rem; color: var(--text-dim); margin: 14px 0 6px; }
  label:first-of-type { margin-top: 0; }
  .two-col { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  #drop {
    border: 2px dashed var(--border); border-radius: 12px; padding: 22px 16px; text-align: center;
    color: var(--text-dim); cursor: pointer; transition: border-color 0.15s, background 0.15s; font-size: 0.9rem;
  }
  #drop:hover { border-color: var(--accent); background: var(--surface-hover); }
  #drop.over { border-color: var(--accent); background: var(--accent-soft); }
  #preview { max-width: 100%; max-height: 220px; border-radius: 10px; margin-top: 12px; display: none; }
  #keyNote { font-size: 0.78rem; color: var(--violet); margin-top: 6px; display: none; }
  #modelNote { font-size: 0.78rem; color: var(--warning); margin-top: 6px; display: none; }
  .row-inline { display: flex; gap: 10px; align-items: center; margin-top: 14px; flex-wrap: wrap; }
  .row-inline label { margin: 0; display: flex; align-items: center; gap: 6px; font-size: 0.85rem; }
  #voiceSelect { width: auto; display: none; }
  button {
    margin-top: 16px; padding: 12px; border: none; border-radius: 10px; width: 100%;
    background: var(--accent); color: #06201c; font-weight: 700; font-size: 0.95rem; cursor: pointer;
  }
  button:disabled { opacity: 0.35; cursor: default; }
  .spinner {
    width: 16px; height: 16px; border-radius: 50%; border: 2px solid var(--border);
    border-top-color: var(--accent); animation: spin 0.7s linear infinite; display: inline-block;
    margin-right: 8px; vertical-align: -3px;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  #result { margin-top: 16px; font-size: 0.9rem; }
  #resultText, .resultText { white-space: pre-wrap; line-height: 1.5; }
  .meta { color: var(--text-dim); font-size: 0.78rem; margin-top: 8px; }
  .fail { color: var(--warning); }
  audio { width: 100%; margin-top: 10px; }
  table { width: 100%; border-collapse: collapse; font-size: 0.78rem; }
  th, td { text-align: left; padding: 7px 6px; border-bottom: 1px solid var(--border); vertical-align: top; }
  th { color: var(--text-dim); font-weight: 600; }
  tr.hrow:hover { background: var(--surface-hover); cursor: pointer; }
  .trunc { max-width: 130px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .empty { color: var(--text-dim); font-style: italic; }
  .topbar { display: flex; align-items: center; gap: 10px; margin-bottom: 4px; }
  #menuBtn {
    width: 38px; height: 38px; flex-shrink: 0; border-radius: 10px; border: 1px solid var(--border);
    background: var(--surface); color: var(--text); font-size: 1.1rem; cursor: pointer; margin-top: 0;
  }
  #menuBtn:hover { background: var(--surface-hover); }
  #menuScrim { display: none; position: fixed; inset: 0; background: #00000090; z-index: 10; }
  #menuDrawer {
    display: none; position: fixed; top: 0; left: 0; bottom: 0; width: 230px; z-index: 11;
    background: #12141a; border-right: 1px solid var(--border); padding: 18px 10px; overflow-y: auto;
  }
  .drawer-title { font-size: 0.75rem; color: var(--text-dim); text-transform: uppercase; letter-spacing: 0.05em; padding: 6px 10px 12px; }
  .drawer-item { padding: 12px 12px; border-radius: 10px; cursor: pointer; color: var(--text-dim); font-size: 0.9rem; margin-bottom: 4px; }
  .drawer-item:hover { background: var(--surface-hover); }
  .drawer-item.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  .explain h3 { font-size: 0.95rem; margin: 18px 0 8px; color: var(--text); }
  .explain h3:first-child { margin-top: 0; }
  .explain p { font-size: 0.88rem; line-height: 1.55; color: var(--text-dim); margin: 0 0 8px; }
  .explain pre {
    background: #00000040; border: 1px solid var(--border); border-radius: 10px; padding: 12px;
    font-size: 0.78rem; line-height: 1.5; overflow-x: auto; color: var(--text); white-space: pre;
  }
  .explain code { color: var(--accent); }
</style>
</head>
<body>
<div id="menuScrim"></div>
<div id="menuDrawer">
  <div class="drawer-title">VLM Playground</div>
  <div class="drawer-item active" data-page="tester">VLM Tester</div>
  <div class="drawer-item" data-page="rag">Mit RAG</div>
  <div class="drawer-item" data-page="explain">Erklaerung</div>
</div>
<main>
  <div class="topbar">
    <button id="menuBtn" type="button">&#9776;</button>
    <h1>VLM Playground</h1>
  </div>
  <p class="subtitle">Verschiedene Anbieter, Modelle, System-Prompts und Fragen an
    demselben Bild vergleichen - unabhaengig von der VR-App. Jeder Durchlauf landet in
    <code>data/vlm_playground_log.csv</code>.</p>

  <div id="page-tester">
    <div class="card">
      <h2>Bild</h2>
      <div class="tabs">
        <div class="tab active" data-tab="sample">Aus Ordner waehlen</div>
        <div class="tab" data-tab="upload">Hochladen</div>
      </div>
      <div id="sampleTab">
        <select id="sampleSelect"><option value="">- Bild waehlen -</option></select>
      </div>
      <div id="uploadTab" style="display:none">
        <div id="drop">Datei hierher ziehen oder klicken</div>
        <input type="file" id="file" style="display:none" accept="image/*">
      </div>
      <img id="preview">
    </div>
  </div>

  <div id="page-rag" style="display:none">
    <div class="card">
      <h2>Bild (aus IDR)</h2>
      <div class="two-col">
        <input type="text" id="idrIdInput" placeholder="z.B. 6001240">
        <button id="idrLoadBtn" type="button" style="margin-top:0">RAG-Kontext laden</button>
      </div>
      <div id="idrStatus" class="meta"></div>
      <div id="idrChunksArea" style="display:none">
        <label>Gefundene Chunks (Ursprung der Antwort)</label>
        <div id="idrChunksList" style="font-size:0.8rem; color:var(--text-dim); max-height:160px; overflow-y:auto;"></div>
        <label for="ragContextText">Zusammengesetzter Kontext (wird vor den System-Prompt gehaengt, editierbar)</label>
        <textarea id="ragContextText" style="min-height:100px"></textarea>
        <div class="row-inline">
          <label><input type="checkbox" id="ragIncludeCheck" checked> RAG-Kontext bei "Fragen" einbeziehen</label>
        </div>
        <button id="compareBtn" type="button">Vergleichen: mit vs. ohne RAG-Kontext</button>
      </div>
      <img id="ragPreview">
    </div>
  </div>

  <div id="page-explain" style="display:none">
    <div class="card explain">
      <h2>Wie das RAG aufgebaut ist</h2>
      <p>Bevor das VLM eine Frage zum gerade geladenen Bild beantwortet, wird vorher
        automatisch in einer Vektordatenbank nach passenden Metadaten-Ausschnitten gesucht
        - die Treffer werden mit in den Prompt gepackt. Grounding statt Raten, statt dass
        das Modell nur aus dem Screenshot vermutet.</p>

      <h3>1. Datenquelle zu Zielschema (Adapter-Pattern)</h3>
      <p>Ein Bild wird nicht als ein grosser Textblock gespeichert, sondern in mehrere
        thematisch getrennte Chunks zerlegt (<code>overview</code>, <code>protocol</code>,
        <code>segmentation</code>, <code>phenotype</code>, <code>genotype</code>,
        <code>resolution</code>) - je nach Frage ist ein anderer Teil relevant. Neue Quelle
        (z.B. Cell Image Library) braucht nur einen neuen Adapter, der Rest bleibt gleich.
        Bisher nur IDR umgesetzt.</p>
      <pre>IDR API (fetch_from_idr.py)
  -&gt; data/idr/&lt;id&gt;/metadata.json
       -&gt; idr_adapter.py: map_idr_metadata()
            -&gt; EIN Bild wird zu MEHREREN Chunks</pre>

      <h3>2. Embedding</h3>
      <p>Kein lokales Modell - laeuft ueber dieselbe ScaDS.AI-Gateway, die auch fuer
        Chat/Vision/STT genutzt wird (<code>Qwen/Qwen3-Embedding-4B</code>, 2560
        Dimensionen, gleicher API-Key aus <code>~/.scadsai-api-key</code>).</p>

      <h3>3. Speicherung</h3>
      <p><code>rag_index.py</code> haelt einen lokalen, persistenten
        <code>chromadb</code>-Index unter <code>data/rag_index/</code>. Embeddings werden
        immer explizit uebergeben, nie von chromadb selbst berechnet - sonst wuerde
        heimlich ein anderes, lokales Default-Modell nachgeladen.</p>

      <h3>4. Abfrage zur Frage-Zeit</h3>
      <p>Bei jeder Frage laufen zwei Suchen gleichzeitig:</p>
      <pre>1) gefiltert auf das AKTUELLE Bild
   -&gt; welcher Teil seiner eigenen Metadaten passt zur Frage

2) ungefiltert ueber den GANZEN Index
   -&gt; findet automatisch aehnliche Chunks aus ANDEREN Bildern
      (z.B. gleiche Faerbung in einer anderen Studie)</pre>
      <p>Beide Ergebnisse werden zu einem Text zusammengesetzt, klar getrennt:</p>
      <pre>Aktuelles Bild:
- Segmentierungsmethode: Manual (Nessys Editor). Segmentierter Kanal: LaminB1.
- growth protocol - Mouse blastocysts (E3.5)
- Diese Probe wurde gefaerbt: Immunostaining: LaminB1 antibody: ab16048 (dilution 1:1000).
Vergleichbare Datensaetze:
- (Bild 6001238) Segmentierungsmethode: Manual (Nessys Editor). Segmentierter Kanal: LaminB1.</pre>
      <p>Reiner Text, keine JSON-Struktur - max. 4 eigene + 3 fremde Chunks, also bis zu
        9 Zeilen. Landet als eigene <code>system</code>-Nachricht vor der eigentlichen
        Frage (in der App), bzw. vor deinen eigenen System-Prompt-Text gehaengt (hier im
        Tester).</p>

      <h3>5. Anbindung an den VR-Chat</h3>
      <pre>VoiceVLMHarness.cs: Frage transkribiert
  -&gt; liest Bild-ID aus geladenem Dateinamen (idr_&lt;id&gt;.nii.gz)
RagClient.cs -&gt; GET upload_server.py:/rag_query?question=...&amp;current_image_id=...
  -&gt; (Python macht dort dieselben zwei Abrufe wie oben)
Kontext-Text zurueck an die App
VLMClient.cs: neuer context-Parameter -&gt; eigene System-Message vor der Frage
  -&gt; https://llm.scads.ai/v1/chat/completions</pre>
      <p>Der Rechner macht die Suche, nicht die Quest - die Brille bleibt ein duenner
        Client, gleiches Prinzip wie beim IDR-Download.</p>

      <h3>Diese drei Menuepunkte hier</h3>
      <p><b>VLM Tester</b>: der urspruengliche Vergleichs-Tester, unabhaengig vom RAG -
        beliebiges Bild, beliebiger Anbieter, keine Metadaten-Anbindung.<br>
        <b>Mit RAG</b>: IDR-Bild-ID eingeben, die gefundenen Chunks und den fertigen
        Kontext-Text sehen (Ursprung nachvollziehbar), per Checkbox in die Frage einbeziehen
        oder per "Vergleichen" direkt mit/ohne Kontext gegenueberstellen.</p>
    </div>
  </div>

  <div id="sharedAskArea">
  <div class="card">
    <h2>Prompt</h2>
    <label for="systemPrompt">System-Prompt / Rolle (optional)</label>
    <textarea id="systemPrompt" placeholder="z.B. Du bist ein erfahrener Mikroskopie-Experte und antwortest praezise und fachlich."></textarea>
    <label for="userPrompt">Frage</label>
    <textarea id="userPrompt">Was siehst du in diesem Bild? Beschreibe sichtbare Strukturen.</textarea>

    <div class="two-col">
      <div>
        <label for="providerSelect">Anbieter</label>
        <select id="providerSelect"></select>
      </div>
      <div>
        <label for="modelSelect">Modell</label>
        <select id="modelSelect"></select>
      </div>
    </div>
    <div id="modelNote"></div>

    <label for="apiKeyInput" id="apiKeyLabel" style="display:none">Eigener API-Key (wird nirgends gespeichert, nur pro Anfrage mitgeschickt)</label>
    <input type="password" id="apiKeyInput" style="display:none" placeholder="API-Key einfuegen">
    <div id="keyNote"></div>

    <div class="row-inline">
      <label><input type="checkbox" id="ttsCheck"> Antwort vorlesen (ueber ScaDS.AI TTS)</label>
      <select id="voiceSelect">
        <option>alloy</option><option>echo</option><option>fable</option>
        <option>onyx</option><option>nova</option><option>shimmer</option>
      </select>
    </div>
    <button id="ask">Fragen</button>
    <div id="result"></div>
    <div id="compareResult" class="two-col"></div>
  </div>

  <div class="card">
    <h2>Verlauf</h2>
    <table id="historyTable">
      <thead><tr><th>Zeit</th><th>Anbieter</th><th>Modell</th><th>Frage</th><th>Antwort</th><th>ms</th></tr></thead>
      <tbody><tr><td colspan="6" class="empty">Noch keine Laeufe</td></tr></tbody>
    </table>
  </div>
  </div>
</main>

<script>
const sampleSelect = document.getElementById('sampleSelect');
const providerSelect = document.getElementById('providerSelect');
const modelSelect = document.getElementById('modelSelect');
const modelNote = document.getElementById('modelNote');
const apiKeyInput = document.getElementById('apiKeyInput');
const apiKeyLabel = document.getElementById('apiKeyLabel');
const keyNote = document.getElementById('keyNote');
const preview = document.getElementById('preview');
const ragPreview = document.getElementById('ragPreview');
const drop = document.getElementById('drop');
const fileInput = document.getElementById('file');
const ttsCheck = document.getElementById('ttsCheck');
const voiceSelect = document.getElementById('voiceSelect');
const askBtn = document.getElementById('ask');
const result = document.getElementById('result');
const compareResult = document.getElementById('compareResult');
const systemPrompt = document.getElementById('systemPrompt');
const userPrompt = document.getElementById('userPrompt');
const historyBody = document.querySelector('#historyTable tbody');
const idrIdInput = document.getElementById('idrIdInput');
const idrLoadBtn = document.getElementById('idrLoadBtn');
const idrStatus = document.getElementById('idrStatus');
const idrChunksArea = document.getElementById('idrChunksArea');
const idrChunksList = document.getElementById('idrChunksList');
const ragContextText = document.getElementById('ragContextText');
const ragIncludeCheck = document.getElementById('ragIncludeCheck');
const compareBtn = document.getElementById('compareBtn');
const menuBtn = document.getElementById('menuBtn');
const menuScrim = document.getElementById('menuScrim');
const menuDrawer = document.getElementById('menuDrawer');
const pageTester = document.getElementById('page-tester');
const pageRag = document.getElementById('page-rag');
const pageExplain = document.getElementById('page-explain');
const sharedAskArea = document.getElementById('sharedAskArea');

let uploadedFile = null;
let providersInfo = {};
let activeImageTab = 'sample';
let ragAvailable = false;

function showPreview(src, img) { img.src = src; img.style.display = 'block'; }

// -- tabs (nur noch sample/upload, "idr" ist jetzt ein eigener menuepunkt) --
document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    activeImageTab = tab.dataset.tab;
    document.getElementById('sampleTab').style.display = activeImageTab === 'sample' ? 'block' : 'none';
    document.getElementById('uploadTab').style.display = activeImageTab === 'upload' ? 'block' : 'none';
  });
});

// -- hamburger menu, drei seiten wie bei handy-apps --
function openMenu() { menuScrim.style.display = 'block'; menuDrawer.style.display = 'block'; }
function closeMenu() { menuScrim.style.display = 'none'; menuDrawer.style.display = 'none'; }
menuBtn.addEventListener('click', openMenu);
menuScrim.addEventListener('click', closeMenu);

function showPage(page) {
  document.querySelectorAll('.drawer-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  pageTester.style.display = page === 'tester' ? 'block' : 'none';
  pageRag.style.display = page === 'rag' ? 'block' : 'none';
  pageExplain.style.display = page === 'explain' ? 'block' : 'none';
  sharedAskArea.style.display = page === 'explain' ? 'none' : 'block';
  if (page === 'tester') {
    const activeTab = document.querySelector('.tab.active');
    activeImageTab = activeTab ? activeTab.dataset.tab : 'sample';
  } else if (page === 'rag') {
    activeImageTab = 'idr';
  }
  closeMenu();
}
document.querySelectorAll('.drawer-item').forEach(el => el.addEventListener('click', () => showPage(el.dataset.page)));

// -- rag verfuegbarkeit (fehlt bei jemandem ohne chromadb/data/rag_index - dann bleibt
// die idr chunk/vergleichs-ui einfach aus, kein crash) --
fetch('/rag_available').then(r => r.json()).then(data => { ragAvailable = data.available; });

idrLoadBtn.addEventListener('click', () => {
  const id = idrIdInput.value.trim();
  if (!id) { idrStatus.textContent = 'Erst eine IDR Bild-ID eintragen.'; return; }
  uploadedFile = null;
  sampleSelect.value = '';
  showPreview('https://idr.openmicroscopy.org/webclient/render_thumbnail/' + encodeURIComponent(id) + '/', ragPreview);

  if (!ragAvailable) {
    idrStatus.textContent = 'Kein RAG-Index verfuegbar (siehe RAG_TESTING.md) - Bild wird ohne Kontext gefragt.';
    idrChunksArea.style.display = 'none';
    return;
  }

  idrStatus.innerHTML = '<span class="spinner"></span>Lade RAG-Kontext ...';
  const q = new URLSearchParams({ image_id: id, question: userPrompt.value });
  fetch('/idr_rag_context?' + q.toString()).then(r => r.json()).then(data => {
    if (data.error) {
      idrStatus.textContent = 'Fehler: ' + data.error;
      idrChunksArea.style.display = 'none';
      return;
    }
    idrStatus.textContent = (data.own_hits.length + data.other_hits.length) + ' Chunks gefunden.';
    idrChunksList.innerHTML = '';
    const renderHit = (hit, label) => {
      const row = document.createElement('div');
      row.style.padding = '4px 0';
      row.textContent = '[' + label + '/' + hit.metadata.field_type + '] (' + hit.distance.toFixed(3) + ') ' + hit.text;
      idrChunksList.appendChild(row);
    };
    data.own_hits.forEach(h => renderHit(h, 'eigenes Bild'));
    data.other_hits.forEach(h => renderHit(h, 'Bild ' + h.metadata.source_id));
    ragContextText.value = data.context;
    idrChunksArea.style.display = 'block';
  }).catch(err => { idrStatus.textContent = 'Fehler: ' + String(err); });
});

// -- samples --
fetch('/samples').then(r => r.json()).then(data => {
  data.samples.forEach(name => {
    const opt = document.createElement('option');
    opt.value = name; opt.textContent = name;
    sampleSelect.appendChild(opt);
  });
});
sampleSelect.addEventListener('change', () => {
  uploadedFile = null;
  if (sampleSelect.value) showPreview('/samples/' + encodeURIComponent(sampleSelect.value), preview);
});

// -- providers + models (abhaengig) --
function loadModels(provider) {
  modelSelect.innerHTML = '<option>lade ...</option>';
  modelNote.style.display = 'none';
  fetch('/models?provider=' + encodeURIComponent(provider)).then(r => r.json()).then(data => {
    modelSelect.innerHTML = '';
    (data.models || []).forEach(id => {
      const opt = document.createElement('option');
      opt.value = id; opt.textContent = id;
      modelSelect.appendChild(opt);
    });
    if (!data.models || !data.models.length) {
      const opt = document.createElement('option');
      opt.textContent = '(keine Modelle gefunden)';
      modelSelect.appendChild(opt);
    }
    if (data.note || data.error) {
      modelNote.textContent = data.note || data.error;
      modelNote.style.display = 'block';
    }
  });

  const info = providersInfo[provider] || {};
  apiKeyInput.style.display = info.needs_key ? 'block' : 'none';
  apiKeyLabel.style.display = info.needs_key ? 'block' : 'none';
  keyNote.style.display = info.needs_key ? 'block' : 'none';
  if (provider === 'gemini') keyNote.textContent = 'Kostenloser Key: https://aistudio.google.com/apikey';
  else if (provider === 'openai') keyNote.textContent = 'Key von https://platform.openai.com/api-keys (kostenpflichtig)';
}

fetch('/providers').then(r => r.json()).then(data => {
  providersInfo = data.providers;
  Object.entries(providersInfo).forEach(([id, info]) => {
    const opt = document.createElement('option');
    opt.value = id; opt.textContent = info.label;
    providerSelect.appendChild(opt);
  });
  loadModels(providerSelect.value);
});
providerSelect.addEventListener('change', () => loadModels(providerSelect.value));

// -- upload --
function chooseFile(f) {
  uploadedFile = f;
  sampleSelect.value = '';
  drop.textContent = f.name;
  const reader = new FileReader();
  reader.onload = () => showPreview(reader.result, preview);
  reader.readAsDataURL(f);
}
drop.addEventListener('click', () => fileInput.click());
fileInput.addEventListener('change', () => { if (fileInput.files[0]) chooseFile(fileInput.files[0]); });
drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('over'); });
drop.addEventListener('dragleave', () => drop.classList.remove('over'));
drop.addEventListener('drop', e => {
  e.preventDefault();
  drop.classList.remove('over');
  if (e.dataTransfer.files[0]) chooseFile(e.dataTransfer.files[0]);
});

ttsCheck.addEventListener('change', () => { voiceSelect.style.display = ttsCheck.checked ? 'inline-block' : 'none'; });

function escapeHtml(s) {
  return (s || '').replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
}

function loadHistory() {
  fetch('/history').then(r => r.json()).then(data => {
    if (!data.rows || !data.rows.length) return;
    historyBody.innerHTML = '';
    data.rows.forEach(row => {
      const tr = document.createElement('tr');
      tr.className = 'hrow';
      const time = (row.timestamp || '').replace('T', ' ').split('.')[0].slice(11);
      tr.innerHTML = '<td>' + time + '</td>' +
        '<td class="trunc">' + escapeHtml(row.provider) + '</td>' +
        '<td class="trunc">' + escapeHtml(row.model) + '</td>' +
        '<td class="trunc">' + escapeHtml(row.user_prompt) + '</td>' +
        '<td class="trunc">' + escapeHtml(row.response) + '</td>' +
        '<td>' + escapeHtml(row.latency_ms) + '</td>';
      tr.addEventListener('click', () => {
        systemPrompt.value = row.system_prompt || '';
        userPrompt.value = row.user_prompt || '';
        if (row.provider && providersInfo[row.provider]) {
          providerSelect.value = row.provider;
          loadModels(row.provider);
        }
      });
      historyBody.appendChild(tr);
    });
  });
}
loadHistory();

function hasImageChosen() {
  if (activeImageTab === 'idr') return !!idrIdInput.value.trim();
  return !!uploadedFile || !!sampleSelect.value;
}

function buildAskForm(includeRag) {
  const form = new FormData();
  form.append('provider', providerSelect.value);
  form.append('model', modelSelect.value);
  form.append('system_prompt', systemPrompt.value);
  form.append('user_prompt', userPrompt.value);
  form.append('api_key', apiKeyInput.value);
  if (activeImageTab === 'idr') form.append('idr_image_id', idrIdInput.value.trim());
  else if (uploadedFile) form.append('file', uploadedFile);
  else form.append('sample_name', sampleSelect.value);
  if (includeRag) form.append('rag_context', ragContextText.value);
  return form;
}

async function runAsk(includeRag) {
  const res = await fetch('/ask', { method: 'POST', body: buildAskForm(includeRag) });
  const data = await res.json();
  if (data.error) throw new Error(data.error);
  return data;
}

async function maybeSpeak(text, container) {
  if (!ttsCheck.checked) return;
  const speakForm = new FormData();
  speakForm.append('text', text);
  speakForm.append('voice', voiceSelect.value);
  const audioRes = await fetch('/speak', { method: 'POST', body: speakForm });
  if (!audioRes.ok) return;
  const blob = await audioRes.blob();
  const audio = document.createElement('audio');
  audio.controls = true;
  audio.autoplay = true;
  audio.src = URL.createObjectURL(blob);
  container.appendChild(audio);
}

askBtn.addEventListener('click', async () => {
  if (!hasImageChosen()) {
    result.innerHTML = '<div class="fail">Erst ein Bild waehlen, hochladen oder eine IDR-ID eintragen.</div>';
    return;
  }
  askBtn.disabled = true;
  compareResult.innerHTML = '';
  result.innerHTML = '<span class="spinner"></span>Frage das Modell ...';

  const includeRag = activeImageTab === 'idr' && ragIncludeCheck.checked && ragContextText.value.trim().length > 0;
  try {
    const data = await runAsk(includeRag);
    let html = '<div id="resultText">' + escapeHtml(data.response) + '</div>' +
      '<div class="meta">' + data.latency_ms + ' ms &middot; ' + escapeHtml(providerSelect.value) + ' / ' + escapeHtml(modelSelect.value) +
      (includeRag ? ' &middot; mit RAG-Kontext' : '') + '</div>';
    result.innerHTML = html;
    loadHistory();
    await maybeSpeak(data.response, result);
  } catch (err) {
    result.innerHTML = '<div class="fail">Fehler: ' + escapeHtml(String(err.message || err)) + '</div>';
  }
  askBtn.disabled = false;
});

compareBtn.addEventListener('click', async () => {
  if (!hasImageChosen()) {
    idrStatus.textContent = 'Erst eine IDR Bild-ID eintragen.';
    return;
  }
  compareBtn.disabled = true;
  askBtn.disabled = true;
  result.innerHTML = '';
  compareResult.innerHTML = '<div><span class="spinner"></span>Ohne RAG ...</div><div><span class="spinner"></span>Mit RAG ...</div>';

  const renderColumn = (title, data, error) => {
    if (error) return '<div><div class="meta">' + escapeHtml(title) + '</div><div class="fail">Fehler: ' + escapeHtml(String(error.message || error)) + '</div></div>';
    return '<div><div class="meta">' + escapeHtml(title) + ' &middot; ' + data.latency_ms + ' ms</div>' +
      '<div class="resultText">' + escapeHtml(data.response) + '</div></div>';
  };

  const [withoutRes, withRes] = await Promise.allSettled([runAsk(false), runAsk(true)]);
  compareResult.innerHTML =
    renderColumn('Ohne RAG-Kontext', withoutRes.value, withoutRes.reason) +
    renderColumn('Mit RAG-Kontext', withRes.value, withRes.reason);
  loadHistory();

  compareBtn.disabled = false;
  askBtn.disabled = false;
});
</script>
</body>
</html>
"""


if __name__ == "__main__":
    import uvicorn

    SAMPLES_DIR.mkdir(parents=True, exist_ok=True)
    ip = _local_ip()
    print(f"Oeffne im WLAN auf Handy/Laptop: http://{ip}:8010")
    uvicorn.run(app, host="0.0.0.0", port=8010)
