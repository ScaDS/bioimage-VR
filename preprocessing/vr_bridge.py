"""bruecke napari und brille, laeuft im upload_server mit (app.include_router).

napari plugin schickt bild plus maske, der server konvertiert ins app format und
merkt sich das als "offen auf der brille". die brille fragt regelmaessig nach und
laedt neues selbst. umgekehrt schickt die brille ihre blickrichtung, napari spiegelt sie,
und korrigierte masken landen hier als neue version fuer "From VR".

ablage: data/napari/<name>/
    <name>.nii.gz            volumen im app format
    <name>_labels.nii.gz     maske wie sie an die brille ging
    <name>_labels_v002.nii.gz  spaetere versionen, z.b. korrekturen aus der brille
    metadata.json            voxelgroesse fuer VolumeView, gleiches schema wie idr
    bridge.json              originalgroesse fuer den weg zurueck nach napari

endpunkte:
    POST /vr/push        napari schickt npz (image, labels, scale)
    GET  /vr/pending     brille fragt nach neuem
    GET  /vr/file        brille laedt volumen, labels, metadata
    POST /vr/view        brille schickt blick und auswahl, ein paar mal pro sekunde
    GET  /vr/view        napari holt den letzten blick
    GET  /vr/labels      napari holt die neueste maske, originalgroesse (z,y,x)
    POST /vr/labels      neue masken version, z.b. aus der brille (stufe 3)
    POST /vr/usb         weiterleitung ueber usb kabel einrichten (adb reverse)
"""

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

NAPARI_ROOT = Path(__file__).resolve().parent.parent / "data" / "napari"

router = APIRouter(prefix="/vr")

_lock = threading.Lock()
# seq ist ein zeitstempel in ms, bleibt dadurch auch ueber server neustarts steigend
_pending = {"seq": 0, "name": None}
_view: dict = {}


def _safe_name(raw: str) -> str:
    """name kommt vom client, darf nicht aus NAPARI_ROOT herausfuehren"""
    cleaned = re.sub(r"[^\w-]", "_", Path(raw or "").name).strip("_")
    return cleaned[:60] or "napari"


def _dir_for(name: str) -> Path:
    return NAPARI_ROOT / name


def _latest_labels(name: str) -> tuple[Path | None, int]:
    """neueste masken version, v1 ist die urspruenglich gesendete"""
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
    """(z,y,x) oder (c,z,y,x), einzelne 2d schicht wird zu z=1"""
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
    """adb reverse fuer eine per usb verbundene brille, gibt None bei erfolg sonst den grund"""
    from deploy_to_headset import find_adb, setup_usb_reverse

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
        _view.clear()
        _view.update(view)
    return JSONResponse({"ok": True})


@router.get("/view")
def get_view() -> JSONResponse:
    with _lock:
        view = dict(_view)
    if view:
        view["age_s"] = time.time() - view["received_at"]
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
    """neue version als nifti im app layout, so wie die brille sie hat.
    auch fuer bilder die nicht ueber napari kamen, dann liegt nur die maske in der ablage"""
    safe = _safe_name(name)
    folder = _dir_for(safe)
    folder.mkdir(parents=True, exist_ok=True)
    _, version = _latest_labels(safe)
    path = folder / f"{safe}_labels_v{version + 1:03d}.nii.gz"
    path.write_bytes(file.file.read())
    return JSONResponse({"name": safe, "version": version + 1})
