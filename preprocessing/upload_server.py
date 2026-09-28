"""lokaler web-upload: bild per browser (handy/laptop im gleichen wlan) hochladen ODER
direkt ein bild aus dem idr datensatz laden, automatisch konvertieren und aufs per usb
verbundene quest pushen.

duenne schicht ueber convert_to_volume.convert(), fetch_from_idr.fetch_one() und den
push-helfern aus deploy_to_headset.py - kein neuer konvertierungs-/push-code, nur die
uebertragung vom user-geraet zu diesem rechner (bzw. das idr-abholen ohne kommandozeile)
kommt neu dazu (der rest lief vorher schon nur per kommandozeile/lokaler datei).

einschraenkung: nur einzeldateien (tif, czi, lsm, png-stapel als eine datei etc.) -
kein ordner-upload, deshalb funktioniert z.b. eine ome-zarr url weiterhin nur ueber
convert_to_volume.py/fetch_from_idr.py direkt, nicht ueber dieses formular.

ist kein quest per usb verbunden (push schlaegt fehl), oeffnet sich das ergebnis
stattdessen automatisch in MRIcroGL (falls installiert) zur schnellen sichtkontrolle
ohne brille - siehe open_in_mricrogl().

usage:
    python upload_server.py
    (zeigt die lokale wlan-adresse an, z.b. http://192.168.1.23:8000)

kein auth-schutz - fuer den vertrauten lokalen wlan-gebrauch gedacht, nicht fuers
oeffentliche internet.
"""

from __future__ import annotations

import json
import re
import shutil
import socket
import subprocess
import tempfile
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import nibabel as nib
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse

from convert_to_volume import convert
from deploy_to_headset import (
    DEFAULT_PACKAGE,
    SAMPLE_DIR,
    check_single_device,
    ensure_api_key_pushed,
    find_adb,
    push_to_device,
)
from fetch_from_idr import DATA_ROOT as IDR_DATA_ROOT
from fetch_from_idr import (
    fetch_one,
    first_dataset_image_id,
    first_project_preview_image_id,
    has_zarr,
    is_3d,
    list_dataset_images_page,
    list_project_datasets_page,
    list_projects_page,
)
from rag_index import query as rag_query_index

app = FastAPI()

PAGE = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BioimageVR Upload</title>
<style>
  :root {
    --bg: #12141a;
    --surface: #ffffff0f;
    --surface-hover: #ffffff1f;
    --border: #ffffff26;
    --accent: #4dccbd;
    --accent-soft: #4dccbd2e;
    --violet: #a88ff2;
    --warning: #f28c59;
    --text: #ffffff;
    --text-dim: #ffffffa6;
    color-scheme: dark; /* laesst den browser native controls (select-dropdown-popup)
      dunkel rendern statt hell/weiss - sonst ist der weisse text dort unlesbar */
  }
  * { box-sizing: border-box; }
  body {
    font-family: -apple-system, "Segoe UI", system-ui, sans-serif;
    background: radial-gradient(circle at 20% 0%, #1b2530 0%, var(--bg) 55%);
    color: var(--text);
    min-height: 100vh;
    margin: 0;
    display: flex;
    justify-content: center;
    padding: 48px 16px;
  }
  main { width: 100%; max-width: 440px; }
  h1 {
    font-size: 1.4rem;
    margin: 0 0 4px;
    background: linear-gradient(90deg, var(--accent), var(--violet));
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
    display: inline-block;
  }
  .subtitle { color: var(--text-dim); font-size: 0.9rem; margin: 0 0 24px; line-height: 1.4; }
  .card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 16px;
    padding: 20px;
    backdrop-filter: blur(6px);
  }
  .tabs { display: flex; gap: 8px; margin-bottom: 14px; }
  .tab { flex: 1; padding: 9px; text-align: center; border-radius: 8px; background: var(--surface); cursor: pointer; color: var(--text-dim); font-size: 0.85rem; }
  .tab.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  #drop {
    border: 2px dashed var(--border);
    border-radius: 12px;
    padding: 36px 16px;
    text-align: center;
    color: var(--text-dim);
    cursor: pointer;
    transition: border-color 0.15s, background 0.15s, transform 0.1s;
  }
  #drop:hover { border-color: var(--accent); background: var(--surface-hover); }
  #drop.over { border-color: var(--accent); background: var(--accent-soft); transform: scale(1.01); }
  #drop .icon { font-size: 1.8rem; display: block; margin-bottom: 8px; opacity: 0.8; }
  #drop .filename { color: var(--text); font-weight: 600; }
  input[type=text], select {
    width: 100%; margin-top: 14px; padding: 11px 12px;
    background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
    color: var(--text); font-size: 0.95rem; font-family: inherit;
  }
  input[type=text]::placeholder { color: var(--text-dim); }
  input[type=text]:focus, select:focus { outline: none; border-color: var(--accent); }
  select option { background: #1b2530; color: var(--text); }
  #idrTab label { display: block; font-size: 0.82rem; color: var(--text-dim); margin: 14px 0 0; }
  #idrTab label:first-child { margin-top: 0; }
  .idr-crumbs { font-size: 0.8rem; color: var(--text-dim); margin-top: 10px; }
  .idr-crumbs .crumb { color: var(--accent); cursor: pointer; }
  .idr-crumbs .crumb:hover { text-decoration: underline; }
  .idr-crumbs .crumb-current { color: var(--text); }
  .idr-grid {
    margin-top: 8px; max-height: 300px; overflow-y: auto; border-radius: 10px; padding: 4px;
    display: grid; grid-template-columns: repeat(auto-fill, minmax(84px, 1fr)); gap: 8px;
  }
  .idr-grid:not(:empty) { border: 1px solid var(--border); }
  .idr-tile {
    cursor: pointer; border: 2px solid transparent; border-radius: 8px; overflow: hidden;
    background: var(--surface); transition: border-color 0.15s;
  }
  .idr-tile:hover { border-color: var(--border); }
  .idr-tile.selected { border-color: var(--accent); }
  .idr-tile.no-zarr { opacity: 0.45; }
  .idr-tile .idr-badge { font-size: 0.58rem; color: var(--warning); padding: 3px 5px 0; }
  .idr-tile img, .idr-tile .idr-noimg {
    width: 100%; height: 64px; object-fit: contain; background: #00000040; display: block;
  }
  .idr-tile .idr-noimg { display: flex; align-items: center; justify-content: center; color: var(--text-dim); font-size: 1.2rem; }
  .idr-tile .lbl {
    font-size: 0.65rem; color: var(--text-dim); padding: 4px 5px; white-space: nowrap;
    overflow: hidden; text-overflow: ellipsis;
  }
  #idrLoadMoreBtn { margin-top: 10px; }
  .idr-empty { grid-column: 1 / -1; color: var(--text-dim); font-size: 0.82rem; font-style: italic; padding: 6px; }
  button {
    width: 100%; margin-top: 14px; padding: 12px; border: none; border-radius: 10px;
    background: var(--accent); color: #06201c; font-weight: 700; font-size: 0.95rem;
    cursor: pointer; transition: opacity 0.15s, transform 0.1s;
  }
  button:disabled { opacity: 0.35; cursor: default; }
  button:not(:disabled):active { transform: scale(0.98); }
  #status { margin-top: 18px; font-size: 0.9rem; }
  .row { display: flex; align-items: flex-start; gap: 8px; padding: 6px 0; }
  .row .mark { flex-shrink: 0; width: 1.2em; }
  .ok { color: var(--accent); }
  .fail { color: var(--warning); }
  .detail { color: var(--text-dim); font-size: 0.82rem; margin-left: 1.8em; word-break: break-word; }
  .spinner {
    width: 18px; height: 18px; border-radius: 50%;
    border: 2px solid var(--border); border-top-color: var(--accent);
    animation: spin 0.7s linear infinite; display: inline-block; margin-right: 8px; vertical-align: -4px;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
</head>
<body>
<main>
  <h1>BioimageVR</h1>
  <p class="subtitle">Mikroskop-Stack hochladen (.tif/.czi/.lsm/...) oder direkt ein Bild
    aus dem IDR-Datensatz laden - wird hier konvertiert und aufs per USB verbundene Quest
    gepusht, oder zur Sichtkontrolle in MRIcroGL geoeffnet falls keins verbunden ist.</p>

  <div class="card">
    <div class="tabs">
      <div class="tab active" data-tab="upload">Datei hochladen</div>
      <div class="tab" data-tab="idr">Aus IDR laden</div>
    </div>

    <div id="uploadTab">
      <div id="drop">
        <span class="icon">&#8593;</span>
        <span id="dropLabel">Datei hierher ziehen oder klicken</span>
      </div>
      <input type="file" id="file" style="display:none">
      <input type="text" id="name" placeholder="Name (optional, sonst vom Dateinamen)">
    </div>

    <div id="idrTab" style="display:none">
      <label for="idrLocalSelect">Bereits lokal heruntergeladen</label>
      <select id="idrLocalSelect"><option value="">- neu von IDR laden -</option></select>

      <label>Durch IDR-Studien stoebern</label>
      <div id="idrCrumbs" class="idr-crumbs">Studien</div>
      <div id="idrBrowseGrid" class="idr-list"></div>
      <button type="button" id="idrLoadMoreBtn" style="display:none">Mehr laden</button>

      <label for="idrIdInput">Oder direkt eine IDR Bild-ID eingeben</label>
      <input type="text" id="idrIdInput" placeholder="z.B. 6001240">
    </div>

    <button id="go" disabled>Hochladen</button>
    <div id="status"></div>
  </div>
</main>

<script>
const drop = document.getElementById('drop');
const dropLabel = document.getElementById('dropLabel');
const fileInput = document.getElementById('file');
const nameInput = document.getElementById('name');
const uploadTab = document.getElementById('uploadTab');
const idrTab = document.getElementById('idrTab');
const idrLocalSelect = document.getElementById('idrLocalSelect');
const idrIdInput = document.getElementById('idrIdInput');
const idrCrumbs = document.getElementById('idrCrumbs');
const idrBrowseGrid = document.getElementById('idrBrowseGrid');
const idrLoadMoreBtn = document.getElementById('idrLoadMoreBtn');
const goBtn = document.getElementById('go');
const status = document.getElementById('status');
let chosenFile = null;
let activeTab = 'upload';
let idrLoadedOnce = false;
let idrLevel = 'projects';
let idrProjectId = null, idrProjectName = '';
let idrDatasetId = null, idrDatasetName = '';
let idrOffset = 0;
const IDR_PAGE = 40;

function escapeHtml(s) {
  return (s || '').replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
}

function idrThumbUrl(id) {
  return 'https://idr.openmicroscopy.org/webclient/render_thumbnail/' + id + '/';
}

function clearIdrSelection() {
  document.querySelectorAll('.idr-tile.selected').forEach(t => t.classList.remove('selected'));
}

function renderIdrCrumbs() {
  let html = '<span class="crumb" data-level="projects">Studien</span>';
  if (idrProjectId !== null) {
    html += ' &rsaquo; <span class="' + (idrLevel === 'datasets' ? 'crumb-current' : 'crumb') +
      '" data-level="datasets">' + escapeHtml(idrProjectName || ('#' + idrProjectId)) + '</span>';
  }
  if (idrDatasetId !== null) {
    html += ' &rsaquo; <span class="crumb-current">' + escapeHtml(idrDatasetName || ('#' + idrDatasetId)) + '</span>';
  }
  idrCrumbs.innerHTML = html;
  idrCrumbs.querySelectorAll('.crumb').forEach(el => {
    el.addEventListener('click', () => {
      const level = el.dataset.level;
      if (level === 'projects') { idrProjectId = null; idrDatasetId = null; }
      else if (level === 'datasets') { idrDatasetId = null; }
      idrLevel = level;
      loadIdrLevel(true);
    });
  });
}

function idrLevelUrl() {
  if (idrLevel === 'projects') return '/idr_list?level=projects';
  if (idrLevel === 'datasets') return '/idr_list?level=datasets&project_id=' + idrProjectId;
  return '/idr_list?level=images&dataset_id=' + idrDatasetId;
}

function addIdrItems(items) {
  items.forEach(item => {
    const el = document.createElement('div');
    const noZarr = item.has_zarr === false;
    el.className = 'idr-tile' + (noZarr ? ' no-zarr' : '');
    el.title = (item.name || ('#' + item.id)) + (noZarr ? ' - kein OME-Zarr-Volumen gefunden' : '');
    const img = item.preview_image_id
      ? '<img loading="lazy" src="' + idrThumbUrl(item.preview_image_id) + '" alt="">'
      : '<div class="idr-noimg">?</div>';
    const badge = noZarr ? '<div class="idr-badge">kein Volumen</div>' : '';
    el.innerHTML = img + badge + '<div class="lbl">' + escapeHtml(item.name || ('#' + item.id)) + '</div>';
    el.addEventListener('click', () => {
      if (idrLevel === 'images') {
        clearIdrSelection();
        el.classList.add('selected');
        idrIdInput.value = item.id;
        idrLocalSelect.value = '';
        updateGoState();
      } else if (idrLevel === 'projects') {
        idrProjectId = item.id; idrProjectName = item.name; idrLevel = 'datasets';
        loadIdrLevel(true);
      } else {
        idrDatasetId = item.id; idrDatasetName = item.name; idrLevel = 'images';
        loadIdrLevel(true);
      }
    });
    idrBrowseGrid.appendChild(el);
  });
}

async function loadIdrLevel(reset) {
  if (reset) {
    idrOffset = 0;
    idrBrowseGrid.innerHTML = '<div class="idr-empty"><span class="spinner"></span>Lade ...</div>';
    renderIdrCrumbs();
  }
  idrLoadMoreBtn.disabled = true;
  try {
    const res = await fetch(idrLevelUrl() + '&offset=' + idrOffset + '&limit=' + IDR_PAGE);
    const data = await res.json();
    if (reset) idrBrowseGrid.innerHTML = '';
    if (data.error) {
      idrBrowseGrid.innerHTML = '<div class="idr-empty">Fehler: ' + escapeHtml(data.error) + '</div>';
      idrLoadMoreBtn.style.display = 'none';
    } else if (!data.items.length && !idrBrowseGrid.children.length) {
      idrBrowseGrid.innerHTML = '<div class="idr-empty">Keine Eintraege gefunden.</div>';
      idrLoadMoreBtn.style.display = 'none';
    } else {
      addIdrItems(data.items);
      idrOffset = data.next_offset;
      idrLoadMoreBtn.style.display = data.has_more ? 'block' : 'none';
    }
  } catch (err) {
    idrBrowseGrid.innerHTML = '<div class="idr-empty">Fehler: ' + String(err) + '</div>';
  }
  idrLoadMoreBtn.disabled = false;
}

idrLoadMoreBtn.addEventListener('click', () => loadIdrLevel(false));

document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    activeTab = tab.dataset.tab;
    uploadTab.style.display = activeTab === 'upload' ? 'block' : 'none';
    idrTab.style.display = activeTab === 'idr' ? 'block' : 'none';
    goBtn.textContent = activeTab === 'upload' ? 'Hochladen' : 'Laden';
    if (activeTab === 'idr' && !idrLoadedOnce) { idrLoadedOnce = true; loadIdrLevel(true); }
    updateGoState();
  });
});

fetch('/idr_local').then(r => r.json()).then(data => {
  (data.images || []).forEach(img => {
    const opt = document.createElement('option');
    opt.value = img.path; opt.textContent = img.label;
    idrLocalSelect.appendChild(opt);
  });
});

function updateGoState() {
  if (activeTab === 'upload') {
    goBtn.disabled = !chosenFile;
  } else {
    goBtn.disabled = !(idrLocalSelect.value || idrIdInput.value.trim());
  }
}
idrLocalSelect.addEventListener('change', () => {
  if (idrLocalSelect.value) { idrIdInput.value = ''; clearIdrSelection(); }
  updateGoState();
});
idrIdInput.addEventListener('input', () => {
  if (idrIdInput.value.trim()) { idrLocalSelect.value = ''; clearIdrSelection(); }
  updateGoState();
});

function chooseFile(f) {
  chosenFile = f;
  dropLabel.innerHTML = 'Ausgewaehlt: <span class="filename">' + f.name + '</span>';
  updateGoState();
}

function row(ok, label, detail) {
  const mark = ok ? '<span class="mark ok">&#10003;</span>' : '<span class="mark fail">&#10007;</span>';
  const cls = ok ? 'ok' : 'fail';
  let html = '<div class="row">' + mark + '<span class="' + cls + '">' + label + '</span></div>';
  if (detail) html += '<div class="detail">' + detail + '</div>';
  return html;
}

function renderResult(data) {
  if (!data.converted) {
    status.innerHTML = row(false, 'Konvertierung fehlgeschlagen', data.error);
    return;
  }
  let html = row(true, 'Konvertiert', 'Form ' + JSON.stringify(data.shape) + ' &middot; ' + data.output);
  if (data.pushed) {
    html += row(true, 'Aufs Quest gepusht');
  } else {
    html += row(false, 'Nicht aufs Quest gepusht', data.push_error);
    if (data.opened_in_mricrogl) {
      html += row(true, 'Stattdessen in MRIcroGL geoeffnet');
    } else if (data.mricrogl_error) {
      html += row(false, 'MRIcroGL-Fallback fehlgeschlagen', data.mricrogl_error);
    }
  }
  status.innerHTML = html;
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

goBtn.addEventListener('click', async () => {
  goBtn.disabled = true;
  let endpoint, form = new FormData();

  if (activeTab === 'upload') {
    if (!chosenFile) { updateGoState(); return; }
    status.innerHTML = '<span class="spinner"></span>Lade hoch und konvertiere ... (kann bei grossen Dateien dauern)';
    endpoint = '/upload';
    form.append('file', chosenFile);
    if (nameInput.value) form.append('name', nameInput.value);
  } else {
    status.innerHTML = '<span class="spinner"></span>Lade von IDR ... (kann bei grossen Bildern dauern)';
    endpoint = '/idr_fetch';
    if (idrLocalSelect.value) form.append('existing_path', idrLocalSelect.value);
    else form.append('image_id', idrIdInput.value.trim());
  }

  try {
    const res = await fetch(endpoint, { method: 'POST', body: form });
    const data = await res.json();
    renderResult(data);
  } catch (err) {
    status.innerHTML = row(false, 'Fehler', String(err));
  }
  updateGoState();
});
</script>
</body>
</html>
"""


@app.get("/", response_class=HTMLResponse)
def index() -> str:
    return PAGE


def push_or_open(output_path: Path) -> dict:
    """gemeinsame push-oder-mricrogl-fallback logik, egal ob die datei per upload oder
    per idr-fetch entstanden ist"""
    result = {}
    try:
        adb = find_adb()
        check_single_device(adb)
        push_to_device(adb, output_path, DEFAULT_PACKAGE)
        ensure_api_key_pushed(adb, DEFAULT_PACKAGE)
        result["pushed"] = True
    except Exception as error:
        # konvertierung/idr-download war trotzdem erfolgreich, nur der push (z.b. kein
        # quest per usb verbunden) hat nicht geklappt - kein grund die ganze anfrage
        # fehlschlagen zu lassen. stattdessen zur schnellen sichtkontrolle in mricrogl
        # oeffnen (siehe docstring oben)
        result["pushed"] = False
        result["push_error"] = str(error)
        try:
            open_in_mricrogl(output_path)
            result["opened_in_mricrogl"] = True
        except Exception as mricrogl_error:
            result["opened_in_mricrogl"] = False
            result["mricrogl_error"] = str(mricrogl_error)
    return result


def _safe_stem(raw: str) -> str:
    """dateiname-baustein von einem client saeubern, bevor er in einen serverseitigen
    pfad einfliesst - wichtig sobald die studie laeuft und beliebige teilnehmergeraete
    das formular ausfuellen (Punkt 15 der wunschliste, STATUS.md 25.08.). Path(...).name
    verwirft jedes verzeichnis-praefix (auch ../../), der rest wird auf harmlose
    zeichen eingedampft - sonst koennte ein praeparierter dateiname/name-feld ausserhalb
    von SAMPLE_DIR schreiben oder bestehende dateien ueberschreiben"""
    name_only = Path(raw).name
    cleaned = re.sub(r"[^\w.-]", "_", name_only).strip("._")
    return cleaned[:80] or "upload"


@app.post("/upload")
def upload(file: UploadFile = File(...), name: str = Form(None)) -> JSONResponse:
    stem = _safe_stem(name or Path(file.filename).stem or "upload")
    output_path = SAMPLE_DIR / f"{stem}.nii.gz"
    if output_path.exists():
        # zwei teilnehmer mit gleich benanntem file (z.b. beide "image.tif" vom handy)
        # sollen sich nicht gegenseitig ueberschreiben
        output_path = SAMPLE_DIR / f"{stem}_{uuid.uuid4().hex[:6]}.nii.gz"

    # original-endung behalten, bioio erkennt das format meist daran
    suffix = Path(file.filename).suffix
    with tempfile.TemporaryDirectory() as tmp_dir:
        staged_path = Path(tmp_dir) / f"{uuid.uuid4().hex}{suffix}"
        with staged_path.open("wb") as f:
            shutil.copyfileobj(file.file, f)

        try:
            volume = convert(str(staged_path), output_path)
        except Exception as error:
            return JSONResponse({"converted": False, "error": str(error)}, status_code=400)

    result = {
        "converted": True,
        "output": str(output_path),
        "shape": list(volume.shape),
    }
    result.update(push_or_open(output_path))
    return JSONResponse(result)


def list_local_idr() -> list[dict]:
    """bereits lokal heruntergeladene idr-bilder auflisten (siehe fetch_from_idr.DATA_ROOT),
    anzeigename aus metadata.json falls vorhanden (wie VolumeLibrary.cs es in der app macht),
    sonst nur der ordnername (= idr bild-id)"""
    entries = []
    if not IDR_DATA_ROOT.is_dir():
        return entries
    for image_dir in sorted(IDR_DATA_ROOT.iterdir()):
        if not image_dir.is_dir():
            continue
        nii_files = sorted(image_dir.glob("*.nii.gz"))
        if not nii_files:
            continue
        display_name = image_dir.name
        metadata_path = image_dir / "metadata.json"
        if metadata_path.is_file():
            try:
                metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
                if metadata.get("name"):
                    display_name = f"{metadata['name']} ({image_dir.name})"
            except Exception:
                pass  # kaputte/fehlende metadata.json ist kein grund den eintrag zu verstecken
        entries.append({"path": str(nii_files[0]), "label": display_name})
    return entries


@app.get("/idr_local")
def idr_local() -> JSONResponse:
    return JSONResponse({"images": list_local_idr()})


def _attach_previews(items: list[dict], resolve) -> None:
    """jedes item bekommt preview_image_id (welches bild als thumbnail dient) UND
    has_zarr (ob dieses vorschau-bild ueberhaupt ein ome-zarr volumen hat), beides im
    selben parallelen durchlauf - sonst wuerde eine seite mit 40 eintraegen 40x
    nacheinander auf idr warten muessen. viele aeltere idr-studien wurden nie zu zarr
    konvertiert (siehe fetch_from_idr.has_zarr) - ohne diese markierung klickt man sich
    sonst quasi zufaellig durch fehlschlaege beim tatsaechlichen laden. ein einzelner
    fehlgeschlagener lookup (leeres dataset/projekt, timeout) liefert einfach
    None/False statt die ganze seite scheitern zu lassen"""
    def resolve_one(item_id: int) -> tuple[int | None, bool]:
        try:
            preview_id = resolve(item_id)
        except Exception:
            return None, False
        if preview_id is None:
            return None, False
        return preview_id, has_zarr(preview_id)

    with ThreadPoolExecutor(max_workers=10) as pool:
        results = list(pool.map(resolve_one, (item["id"] for item in items)))
    for item, (preview_id, zarr_ok) in zip(items, results):
        item["preview_image_id"] = preview_id
        item["has_zarr"] = zarr_ok


def _scan_usable_images(dataset_id: int, start_offset: int, limit: int) -> tuple[list[dict], int, int, bool]:
    """wie list_dataset_images_page, aber ueberspringt bilder ohne zarr-volumen UND ohne
    echten 3d-stack (SizeZ<=1) - die grosse mehrheit der 2d-objekttraeger-scans soll erst
    gar nicht mehr in der galerie auftauchen (vorher nur abgedunkelt mit badge). scannt
    dafuer notfalls mehrere rohe seiten hintereinander bis 'limit' brauchbare bilder
    zusammen sind oder das dataset zuende ist. offset/total bleiben in der ROHEN
    (ungefilterten) indexzaehlung, next_offset sagt dem client wo die naechste 'Mehr
    laden'-anfrage weitermachen soll - kann nicht einfach offset+len(items) sein, weil
    gefilterte items die zaehlung sonst durcheinanderbringen wuerden (client wuerde
    sonst dieselben rohen bilder mehrfach anfragen bzw. welche ueberspringen)"""
    usable: list[dict] = []
    offset = start_offset
    raw_total = 0

    while len(usable) < limit:
        page_items, raw_total = list_dataset_images_page(dataset_id, offset=offset, limit=40)
        if not page_items:
            break
        offset += len(page_items)

        def check(item: dict) -> tuple[dict, bool]:
            image_id = item["id"]
            return item, has_zarr(image_id) and is_3d(image_id)

        with ThreadPoolExecutor(max_workers=10) as pool:
            checked = list(pool.map(check, page_items))

        for item, ok in checked:
            if not ok:
                continue
            item["has_zarr"] = True
            usable.append(item)
            if len(usable) >= limit:
                break

        if offset >= raw_total:
            break

    return usable[:limit], offset, raw_total, offset < raw_total


@app.get("/idr_list")
def idr_list(
    level: str,
    project_id: int | None = None,
    dataset_id: int | None = None,
    offset: int = 0,
    limit: int = 40,
) -> JSONResponse:
    """einheitlicher endpunkt fuer alle drei durchstoeber-ebenen (studien -> datasets ->
    bilder), damit das frontend nicht drei fast identische loader braucht und man clean
    von den obersten idr-studien bis zum einzelnen bild durchklicken kann, ohne vorher
    irgendeine id kennen zu muessen. jedes item bekommt ein preview_image_id (siehe
    _attach_previews) - der browser laedt das thumbnail dann direkt von idr selbst
    (siehe PAGE, <img src> ist nicht von cors betroffen, kein bild-proxy noetig).

    'next_offset'/'has_more' statt nur 'total' im ergebnis, weil level=images gefiltert
    zurueckkommt (siehe _scan_usable_images) - offset und anzahl zurueckgegebener items
    laufen dort auseinander. bei projects/datasets (ungefiltert) ist next_offset einfach
    offset+len(items), identisch zum alten client-seitigen offset += items.Count"""
    try:
        if level == "projects":
            items, total = list_projects_page(offset=offset, limit=limit)
            _attach_previews(items, first_project_preview_image_id)
            next_offset = offset + len(items)
            has_more = next_offset < total
        elif level == "datasets":
            if project_id is None:
                return JSONResponse({"items": [], "total": 0, "error": "project_id fehlt."}, status_code=400)
            items, total = list_project_datasets_page(project_id, offset=offset, limit=limit)
            _attach_previews(items, first_dataset_image_id)
            next_offset = offset + len(items)
            has_more = next_offset < total
        elif level == "images":
            if dataset_id is None:
                return JSONResponse({"items": [], "total": 0, "error": "dataset_id fehlt."}, status_code=400)
            items, total, next_offset, has_more = _scan_usable_images(dataset_id, offset, limit)
        else:
            return JSONResponse({"items": [], "total": 0, "error": f"unbekannte ebene: {level}"}, status_code=400)
    except Exception as error:
        return JSONResponse({"items": [], "total": 0, "error": str(error)}, status_code=502)
    return JSONResponse({"items": items, "total": total, "next_offset": next_offset, "has_more": has_more})


@app.post("/idr_fetch")
def idr_fetch(image_id: str = Form(""), existing_path: str = Form("")) -> JSONResponse:
    """entweder ein schon lokal vorhandenes idr-bild erneut verwenden (existing_path,
    aus /idr_local) oder ein neues per id von idr laden (image_id, ruft fetch_one auf -
    das gleiche was fetch_from_idr.py auf der kommandozeile macht)"""
    if existing_path:
        output_path = Path(existing_path)
        try:
            output_path = output_path.resolve()
            output_path.relative_to(IDR_DATA_ROOT.resolve())
        except ValueError:
            return JSONResponse({"converted": False, "error": "Ungueltiger Pfad."}, status_code=400)
        if not output_path.is_file():
            return JSONResponse({"converted": False, "error": f"Datei nicht gefunden: {output_path}"}, status_code=400)
    elif image_id.strip():
        try:
            image_id_int = int(image_id.strip())
        except ValueError:
            return JSONResponse({"converted": False, "error": "IDR Bild-ID muss eine Zahl sein."}, status_code=400)
        try:
            output_path = fetch_one(image_id_int)
        except Exception as error:
            return JSONResponse({"converted": False, "error": str(error)}, status_code=400)
    else:
        return JSONResponse({"converted": False, "error": "IDR Bild-ID eingeben oder ein lokales Bild waehlen."}, status_code=400)

    # nur den header lesen (nib.load ist lazy), fuers shape-anzeigen im formular -
    # kein grund die vollen daten nochmal zu laden, das passiert schon in convert()/fetch_one()
    shape = list(nib.load(str(output_path)).shape)

    result = {"converted": True, "output": str(output_path), "shape": shape}
    result.update(push_or_open(output_path))
    return JSONResponse(result)


@app.get("/vr_idr_file", response_model=None)
def vr_idr_file(image_id: int):
    """direkter download fuer die VR app selbst (siehe unity IdrClient.cs) - kein
    adb push/mricrogl-fallback, die app laedt die fertige .nii.gz per http direkt
    aufs headset statt ueber diesen rechner-browser zu gehen. nutzt den lokalen
    cache falls das bild schon frueher geladen wurde (gleiches muster wie
    idr_local/existing_path), sonst frisch per fetch_one()"""
    cached_path = IDR_DATA_ROOT / str(image_id) / f"idr_{image_id}.nii.gz"
    if cached_path.is_file():
        output_path = cached_path
    else:
        try:
            output_path = fetch_one(image_id)
        except Exception as error:
            return JSONResponse({"error": str(error)}, status_code=400)

    return FileResponse(output_path, media_type="application/octet-stream", filename=output_path.name)


@app.get("/rag_query")
def rag_query(question: str, current_image_id: int) -> JSONResponse:
    """rag kontext fuers gerade in der app geladene bild (siehe unity RagClient.cs) -
    zwei abrufe: gefiltert auf current_image_id (welcher teil seiner eigenen metadaten
    passt zur frage) und ungefiltert ueber den ganzen index (findet automatisch
    aehnliche chunks aus anderen bildern, z.b. gleiche faerbung). baut daraus einen
    fertigen text zum voranstellen an den vlm prompt, siehe VLMClient.cs context param"""
    try:
        own_hits = rag_query_index(question, n_results=4, source_id=current_image_id)
        other_hits = rag_query_index(question, n_results=4)
    except Exception as error:
        return JSONResponse({"context": "", "error": str(error)}, status_code=502)

    other_hits = [hit for hit in other_hits if hit["metadata"]["source_id"] != str(current_image_id)]

    parts = []
    if own_hits:
        parts.append("Aktuelles Bild:")
        parts.extend(f"- {hit['text']}" for hit in own_hits)
    if other_hits:
        parts.append("Vergleichbare Datensaetze:")
        parts.extend(f"- (Bild {hit['metadata']['source_id']}) {hit['text']}" for hit in other_hits[:3])

    return JSONResponse({"context": "\n".join(parts)})


def find_mricrogl() -> Path | None:
    """sucht MRIcroGL.exe an ein paar ueblichen stellen (offizieller installer legt es
    unter Program Files ab, die haeufige zip-variante landet direkt in Downloads)"""
    candidates = [
        Path.home() / "Downloads" / "MRIcroGL_windows" / "MRIcroGL" / "MRIcroGL.exe",
        Path(r"C:\Program Files\MRIcroGL\MRIcroGL.exe"),
        Path(r"C:\MRIcroGL\MRIcroGL.exe"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate

    on_path = shutil.which("MRIcroGL")
    return Path(on_path) if on_path else None


def open_in_mricrogl(nifti_path: Path) -> None:
    """oeffnet die datei direkt im 3d-render-view (nicht der 2d-slice-standardansicht) -
    ueber ein generiertes python-scriptlet, das ist MRIcroGLs eigener scripting-mechanismus
    (gl.loadimage + gl.view(64) fuers rendering, siehe PYTHON.md/COMMANDS.md im MRIcroGL
    repo). MRIcroGL bleibt offen, kein subprocess.run/warten - reine GUI-app"""
    exe = find_mricrogl()
    if exe is None:
        raise FileNotFoundError("MRIcroGL.exe nicht gefunden (uebliche Speicherorte geprueft).")

    script = tempfile.NamedTemporaryFile(mode="w", suffix=".py", delete=False, encoding="utf-8")
    with script:
        script.write(f"import gl\ngl.loadimage(r'{nifti_path}')\ngl.view(64)\n")

    subprocess.Popen([str(exe), script.name])


def _local_ip() -> str:
    """beste schaetzung der lan-ip, ohne wirklich daten zu senden"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        s.close()


if __name__ == "__main__":
    import uvicorn

    SAMPLE_DIR.mkdir(parents=True, exist_ok=True)
    ip = _local_ip()
    print(f"Oeffne im WLAN auf Handy/Laptop: http://{ip}:8000")
    uvicorn.run(app, host="0.0.0.0", port=8000)
