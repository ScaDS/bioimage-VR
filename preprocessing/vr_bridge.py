# bruecke napari und brille unter /vr, ablage data/napari/<name>, endpunkte siehe die router funktionen

from __future__ import annotations

import io
import json
import re
import threading
import time
from pathlib import Path

import numpy as np
from fastapi import APIRouter, File, Form, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse, Response

from convert_labels import labels_path_for, read_labels_zyx, resize_labels_nearest, write_labels_array
from convert_to_volume import convert_array
from deploy_to_headset import find_adb, setup_usb_reverse
from hpc import volume_to_czyx
from segment_anatomy import metadata_path_for, names_path_for, read_voxel_size

DATA_ROOT = Path(__file__).resolve().parent.parent / "data"
NAPARI_ROOT = DATA_ROOT / "napari"

router = APIRouter(prefix="/vr")

_lock = threading.Lock()
# seq ist ein zeitstempel in ms, bleibt dadurch auch ueber server neustarts steigend
_pending = {"seq": 0, "name": None}
_view: dict = {}
# ansicht aus napari, seq steigt mit jeder aenderung, die brille meldet zurueck welche sie hat
_napari_view: dict = {"seq": 0}
# geaenderte ausschnitte der maske aus napari, nur im speicher
_patches: dict = {"name": None, "seq": 0, "items": []}
PATCH_KEEP_BYTES = 256 * 1024 * 1024
# zuletzt aus der brille gespeicherte maske, napari holt sie dann von selbst
_labels_saved: dict = {"name": None, "version": 0}
# fenster von napari als jpeg, nur das neueste
_screen: dict = {"seq": 0, "jpeg": b""}
_screen_changed = threading.Condition(_lock)
# controller strahl auf der leinwand als maus ereignisse fuer napari, jedes hat eine laufende nummer
_pointer: dict = {"seq": 0, "items": []}
_pointer_changed = threading.Condition(_lock)
POINTER_KEEP = 500
# laenger haelt eine wartende anfrage der brille keinen server thread fest
SCREEN_WAIT_MAX_S = 2.0


def _safe_name(raw: str) -> str:
    # name kommt vom client, darf nicht aus NAPARI_ROOT herausfuehren
    cleaned = re.sub(r"[^\w-]", "_", Path(raw or "").name).strip("_")
    return cleaned[:60] or "napari"


def _dir_for(name: str) -> Path:
    return NAPARI_ROOT / name


def _latest_labels(name: str) -> tuple[Path | None, int]:
    # neueste masken version, v1 ist die urspruenglich gesendete
    folder = _dir_for(name)
    versions = []
    for path in folder.glob(f"{name}_labels_v*.nii.gz"):
        match = re.search(r"_labels_v(\d+)\.nii\.gz$", path.name)
        if match:
            versions.append((int(match.group(1)), path))
    if versions:
        version, path = max(versions)
        return path, version
    base = labels_path_for(folder / f"{name}.nii.gz")
    return (base, 1) if base.is_file() else (None, 0)


def as_czyx(image: np.ndarray) -> np.ndarray:
    # (z,y,x) oder (c,z,y,x), einzelne 2d schicht wird zu z=1
    if image.ndim == 2:
        image = image[None]
    if image.ndim == 3:
        image = image[None]
    if image.ndim != 4:
        raise ValueError(f"Expected image as (z,y,x) or (c,z,y,x), got {image.shape}")
    return image


@router.post("/push")
def push(bundle: UploadFile = File(...), name: str = Form("napari"), denoise: bool = Form(False)) -> JSONResponse:
    safe = _safe_name(name)
    try:
        data = np.load(io.BytesIO(bundle.file.read()), allow_pickle=False)
        image = as_czyx(data["image"])
        labels = data["labels"] if "labels" in data.files else None
        scale = [float(s) for s in data["scale"]] if "scale" in data.files else [1.0, 1.0, 1.0]
        channel_names = [str(c) for c in data["channel_names"]] if "channel_names" in data.files else []
    except Exception as error:
        return JSONResponse({"error": f"Paket nicht lesbar: {error}"}, status_code=400)

    original_zyx = list(image.shape[1:])
    if labels is not None and labels.ndim == 2:
        labels = labels[None]
    if labels is not None and list(labels.shape) != original_zyx:
        return JSONResponse(
            {"error": f"Maske {list(labels.shape)} passt nicht zum Bild {original_zyx} (z,y,x)"},
            status_code=400)

    folder = _dir_for(safe)
    folder.mkdir(parents=True, exist_ok=True)
    # alte versionen gehoeren zu einem anderen stand, neu anfangen
    for old in folder.glob(f"{safe}_labels_v*.nii.gz"):
        old.unlink()

    volume_path = folder / f"{safe}.nii.gz"
    try:
        written = convert_array(image, volume_path, denoise=denoise)
    except Exception as error:
        return JSONResponse({"error": f"Konvertierung fehlgeschlagen: {error}"}, status_code=400)

    written_zyx = list(written.shape[:3])
    # verkleinert heisst auch die voxel sind groesser geworden
    factor = [o / w for o, w in zip(original_zyx, written_zyx)]
    voxel_zyx = [s * f for s, f in zip(scale, factor)]

    labels_path = labels_path_for(volume_path)
    if labels is not None:
        write_labels_array(resize_labels_nearest(labels, tuple(written_zyx)), volume_path)
    elif labels_path.is_file():
        labels_path.unlink()

    metadata = {
        "name": safe,
        "source": "napari",
        "size": {"x": written_zyx[2], "y": written_zyx[1], "z": written_zyx[0], "c": int(image.shape[0])},
        "voxel_size_um": {"x": voxel_zyx[2], "y": voxel_zyx[1], "z": voxel_zyx[0]},
        "channels": channel_names,
    }
    (folder / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    bridge = {"original_shape_zyx": original_zyx, "written_shape_zyx": written_zyx, "scale_zyx": scale}
    (folder / "bridge.json").write_text(json.dumps(bridge, indent=2), encoding="utf-8")

    with _lock:
        _pending["seq"] = int(time.time() * 1000)
        _pending["name"] = safe

    return JSONResponse({
        "name": safe,
        "shape_zyx": written_zyx,
        "downsampled": written_zyx != original_zyx,
        "has_labels": labels is not None,
    })


def setup_usb(port: int = 8000) -> str | None:
    # adb reverse fuer eine per usb verbundene brille, gibt None bei erfolg sonst den grund
    try:
        setup_usb_reverse(find_adb(), port)
        return None
    except Exception as error:
        detail = getattr(error, "stderr", None)
        return (detail.decode(errors="ignore").strip() if isinstance(detail, bytes) and detail else str(error))


@router.post("/usb")
def usb() -> JSONResponse:
    error = setup_usb()
    if error:
        return JSONResponse({"ok": False, "error": error}, status_code=400)
    return JSONResponse({"ok": True})


@router.get("/pending")
def pending() -> JSONResponse:
    with _lock:
        state = dict(_pending)
    if state["name"]:
        labels_path, _ = _latest_labels(state["name"])
        state["has_labels"] = labels_path is not None
    return JSONResponse(state)


@router.get("/file", response_model=None)
def file(name: str, kind: str):
    safe = _safe_name(name)
    folder = _dir_for(safe)
    if kind == "volume":
        path = folder / f"{safe}.nii.gz"
    elif kind == "labels":
        path, _ = _latest_labels(safe)
    elif kind == "metadata":
        path = folder / "metadata.json"
    else:
        return JSONResponse({"error": f"unbekannte art '{kind}'"}, status_code=400)
    if path is None or not path.is_file():
        return JSONResponse({"error": "nicht vorhanden"}, status_code=404)
    return FileResponse(path, media_type="application/octet-stream", filename=path.name)


@router.post("/view")
async def post_view(request: Request) -> JSONResponse:
    try:
        view = await request.json()
    except Exception:
        return JSONResponse({"error": "kein json"}, status_code=400)
    view["received_at"] = time.time()
    with _lock:
        napari_view = dict(_napari_view)
        # blick der brille verwerfen bis sie die neueste napari ansicht hat, sonst springt napari zurueck
        if int(view.get("napariSeq") or 0) >= napari_view.get("camSeq", 0):
            _view.clear()
            _view.update(view)
        patch_name, patch_seq = _patches["name"], _patches["seq"]
    return JSONResponse({"ok": True, "napari": napari_view, "patchName": patch_name or "", "patchSeq": patch_seq})


def _headset_shape(name: str, napari_zyx: tuple[int, int, int]) -> tuple[int, int, int]:
    # groesse der maske in der brille, bei verkleinertem bild aus bridge.json
    bridge_path = _dir_for(name) / "bridge.json"
    if bridge_path.is_file():
        bridge = json.loads(bridge_path.read_text(encoding="utf-8"))
        if tuple(bridge["original_shape_zyx"]) == tuple(napari_zyx):
            return tuple(bridge["written_shape_zyx"])
    return tuple(napari_zyx)


def _resize_patch(block: np.ndarray, origin, napari_zyx, headset_zyx):
    # ausschnitt aufs raster der brille, zuordnung wie resize_labels_nearest, None ohne treffer
    picks, corner = [], []
    for axis in range(3):
        old, new = napari_zyx[axis], headset_zyx[axis]
        targets = np.arange(new)
        sources = np.minimum((targets * (old / new)).astype(np.int64), old - 1)
        inside = (sources >= origin[axis]) & (sources < origin[axis] + block.shape[axis])
        if not inside.any():
            return None
        corner.append(int(targets[inside][0]))
        picks.append(sources[inside] - origin[axis])
    return corner, block[np.ix_(*picks)]


@router.post("/labels_patch")
async def post_labels_patch(request: Request, name: str, z: int, y: int, x: int, d: int, h: int, w: int,
                            sz: int, sy: int, sx: int) -> JSONResponse:
    # ausschnitt der maske aus napari, uint16 in der reihenfolge (z,y,x), ecke z,y,x in der ganzen maske sz,sy,sx
    safe = _safe_name(name)
    body = await request.body()
    if d <= 0 or h <= 0 or w <= 0 or len(body) != d * h * w * 2:
        return JSONResponse({"error": "ausschnitt passt nicht zur angegebenen groesse"}, status_code=400)
    block = np.frombuffer(body, dtype="<u2").reshape(d, h, w)
    origin = [z, y, x]
    napari_zyx = (sz, sy, sx)
    headset_zyx = _headset_shape(safe, napari_zyx)
    if headset_zyx != napari_zyx:
        resized = _resize_patch(block, origin, napari_zyx, headset_zyx)
        if resized is None:
            return JSONResponse({"ok": True, "seq": 0})
        origin, block = resized
    data = np.ascontiguousarray(block).tobytes()
    # die brille zaehlt x,y,z und legt x am schnellsten ab, das ist dieselbe byte folge
    box = [origin[2], origin[1], origin[0], block.shape[2], block.shape[1], block.shape[0]]
    with _lock:
        if _patches["name"] != safe:
            _patches["name"] = safe
            _patches["items"] = []
        _patches["seq"] = max(int(time.time() * 1000), _patches["seq"] + 1)
        _patches["items"].append((_patches["seq"], box, data))
        while len(_patches["items"]) > 1 and sum(len(item[2]) for item in _patches["items"]) > PATCH_KEEP_BYTES:
            _patches["items"].pop(0)
        seq = _patches["seq"]
    return JSONResponse({"ok": True, "seq": seq})


@router.get("/labels_patch", response_model=None)
def get_labels_patch(name: str, after: int = 0):
    # der aelteste ausschnitt nach after, 204 wenn die brille alle hat
    safe = _safe_name(name)
    with _lock:
        items = _patches["items"] if _patches["name"] == safe else []
        found = next((item for item in items if item[0] > after), None)
    if found is None:
        return Response(status_code=204)
    seq, box, data = found
    return Response(data, media_type="application/octet-stream",
                    headers={"X-Seq": str(seq), "X-Box": ",".join(str(v) for v in box)})


@router.post("/napari_view")
async def post_napari_view(request: Request) -> JSONResponse:
    try:
        view = await request.json()
    except Exception:
        return JSONResponse({"error": "kein json"}, status_code=400)
    with _lock:
        view["seq"] = int(time.time() * 1000)
        # nur eine neue blickrichtung zaehlt als camSeq, die letzte bleibt stehen
        if "camForward" in view:
            view["camSeq"] = view["seq"]
        else:
            view["camSeq"] = _napari_view.get("camSeq", 0)
            for key in ("camForward", "camUp"):
                if key in _napari_view:
                    view[key] = _napari_view[key]
        _napari_view.clear()
        _napari_view.update(view)
    return JSONResponse({"ok": True, "seq": view["seq"]})


@router.post("/screen")
async def post_screen(request: Request) -> JSONResponse:
    jpeg = await request.body()
    with _lock:
        _screen["seq"] = max(int(time.time() * 1000), _screen["seq"] + 1)
        _screen["jpeg"] = jpeg
        _screen_changed.notify_all()
    return JSONResponse({"ok": True})


@router.post("/pointer")
async def post_pointer(request: Request) -> JSONResponse:
    try:
        events = (await request.json()).get("events") or []
    except Exception:
        return JSONResponse({"error": "kein json"}, status_code=400)
    with _pointer_changed:
        for event in events:
            _pointer["seq"] += 1
            _pointer["items"].append((_pointer["seq"], event))
        del _pointer["items"][:-POINTER_KEEP]
        _pointer_changed.notify_all()
    return JSONResponse({"ok": True})


@router.get("/pointer")
def get_pointer(after: int = -1, wait: float = 0.0) -> JSONResponse:
    # ereignisse nach after. unter 0 nur den stand, so faengt napari nicht mit alten klicks an
    with _pointer_changed:
        if after >= 0 and wait > 0:
            _pointer_changed.wait_for(lambda: _pointer["seq"] > after, timeout=min(wait, SCREEN_WAIT_MAX_S))
        seq = _pointer["seq"]
        # nach einem server neustart zaehlt er von vorn, dann auch nur den stand
        events = [event for number, event in _pointer["items"] if number > after] if 0 <= after <= seq else []
    return JSONResponse({"seq": seq, "events": events})


@router.get("/screen", response_model=None)
def get_screen(after: int = 0, wait: float = 0.0):
    with _screen_changed:
        # warten statt die brille staendig nachfragen lassen, so kommt jedes bild sofort an
        if wait > 0:
            _screen_changed.wait_for(lambda: _screen["seq"] > after, timeout=min(wait, SCREEN_WAIT_MAX_S))
        seq, jpeg = _screen["seq"], _screen["jpeg"]
    if seq <= after or not jpeg:
        return Response(status_code=204)
    return Response(jpeg, media_type="image/jpeg", headers={"X-Seq": str(seq)})


def _find_volume(name: str) -> Path | None:
    # volumen mit diesem namen irgendwo unter data, sicherungen ausgenommen
    candidates = [p for p in DATA_ROOT.rglob(f"{name}.nii.gz") if "backup" not in str(p).lower()]
    return max(candidates, key=lambda p: p.stat().st_mtime) if candidates else None


@router.get("/current", response_model=None)
def current(name: str):
    # bild, maske und namen so wie die brille sie zeigt, fuer napari als (c,z,y,x)
    safe = _safe_name(name)
    volume_path = _find_volume(safe)
    if volume_path is None:
        return JSONResponse({"error": f"'{safe}' liegt nicht auf dem PC unter data"}, status_code=404)

    arrays = {"image": volume_to_czyx(volume_path)}
    metadata_path = metadata_path_for(volume_path)
    metadata = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path else {}
    voxel, unit = read_voxel_size(metadata)
    arrays["scale"] = np.asarray([1.0, 1.0, 1.0] if voxel is None else [voxel[2], voxel[1], voxel[0]])
    arrays["unit"] = np.asarray(unit or "")
    arrays["channel_names"] = np.asarray([str(c) for c in metadata.get("channels") or []])

    labels_path = labels_path_for(volume_path)
    if labels_path.is_file():
        arrays["labels"] = read_labels_zyx(labels_path)
        names_path = names_path_for(volume_path)
        if names_path.is_file():
            entries = json.loads(names_path.read_text(encoding="utf-8")).get("labels", [])
            arrays["label_ids"] = np.asarray([e["id"] for e in entries], dtype=np.int32)
            arrays["label_names"] = np.asarray([e["name"] for e in entries])

    buffer = io.BytesIO()
    np.savez_compressed(buffer, **arrays)
    return Response(buffer.getvalue(), media_type="application/octet-stream")


@router.get("/view")
def get_view() -> JSONResponse:
    with _lock:
        view = dict(_view)
        saved = dict(_labels_saved)
    if view:
        view["age_s"] = time.time() - view["received_at"]
        view["labelsSaved"] = saved
    return JSONResponse(view)


@router.get("/labels", response_model=None)
def get_labels(name: str):
    safe = _safe_name(name)
    labels_path, version = _latest_labels(safe)
    if labels_path is None:
        return JSONResponse({"error": "keine Maske vorhanden"}, status_code=404)

    labels = read_labels_zyx(labels_path)
    bridge_path = _dir_for(safe) / "bridge.json"
    scale = [1.0, 1.0, 1.0]
    if bridge_path.is_file():
        bridge = json.loads(bridge_path.read_text(encoding="utf-8"))
        labels = resize_labels_nearest(labels, tuple(bridge["original_shape_zyx"]))
        scale = bridge["scale_zyx"]

    buffer = io.BytesIO()
    np.savez_compressed(buffer, labels=labels, scale=np.asarray(scale), version=np.asarray(version))
    return Response(buffer.getvalue(), media_type="application/octet-stream")


@router.post("/labels")
def post_labels(name: str, file: UploadFile = File(...)) -> JSONResponse:
    # neue masken version als nifti im app layout, auch fuer bilder die nicht aus napari kamen
    safe = _safe_name(name)
    folder = _dir_for(safe)
    folder.mkdir(parents=True, exist_ok=True)
    _, version = _latest_labels(safe)
    path = folder / f"{safe}_labels_v{version + 1:03d}.nii.gz"
    path.write_bytes(file.file.read())
    with _lock:
        _labels_saved["name"] = safe
        _labels_saved["version"] = version + 1
        # die gespeicherte maske enthaelt alle ausschnitte schon
        if _patches["name"] == safe:
            _patches["items"] = []
    return JSONResponse({"name": safe, "version": version + 1})
