# logik ohne napari und qt, gegenstueck zu vr_bridge.py und VrBridge.cs

from __future__ import annotations

import atexit
import io
import math
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

import numpy as np
import requests

MAX_CHANNELS = 3
# liegt nur bei einer installation aus dem repo (pip install -e) an dieser stelle
SERVER_SCRIPT = Path(__file__).resolve().parents[3] / "preprocessing" / "upload_server.py"
SERVER_PORT = 8000
SERVER_LOG = Path(tempfile.gettempdir()) / "bioimage_vr_server.log"

# eine verbindung je thread offen halten statt fuer jede anfrage eine neue
_local = threading.local()


def _http() -> requests.Session:
    if not hasattr(_local, "session"):
        _local.session = requests.Session()
    return _local.session


def _url(server: str) -> str:
    # localhost als 127.0.0.1, windows probiert sonst erst ipv6 und wartet 2 s
    return re.sub(r"//localhost(?=[:/]|$)", "//127.0.0.1", server.rstrip("/"))


def to_czyx(array: np.ndarray, rgb: bool = False) -> np.ndarray:
    # ein layer als (c,z,y,x), rgb hinten, 2d wird z=1, zeit auf ersten index
    array = np.asarray(array)
    if rgb:
        array = np.moveaxis(array[..., :3], -1, 0)
        if array.ndim == 3:
            array = array[:, None]
        return array
    while array.ndim > 4:
        array = array[0]
    if array.ndim == 2:
        array = array[None]
    if array.ndim == 3:
        array = array[None]
    return array


def build_bundle(channels: list[np.ndarray], labels: np.ndarray | None,
                 scale_zyx: tuple[float, float, float], channel_names: list[str]) -> bytes:
    # npz fuer POST /vr/push, kanaele muessen dieselbe raeumliche groesse haben
    stacked = np.concatenate([to_czyx(c) for c in channels], axis=0)[:MAX_CHANNELS]
    spatial = stacked.shape[1:]
    arrays = {
        "image": stacked,
        "scale": np.asarray(scale_zyx, dtype=np.float64),
        "channel_names": np.asarray(channel_names[:MAX_CHANNELS]),
    }
    if labels is not None:
        labels = np.asarray(labels)
        if labels.ndim == 2:
            labels = labels[None]
        if labels.shape != spatial:
            raise ValueError(f"Maske {labels.shape} passt nicht zum Bild {spatial} (z,y,x)")
        if labels.max() > 65535 or labels.min() < 0:
            raise ValueError("Label-IDs muessen zwischen 0 und 65535 liegen")
        arrays["labels"] = labels.astype(np.uint16)

    buffer = io.BytesIO()
    np.savez_compressed(buffer, **arrays)
    return buffer.getvalue()


def push(server: str, bundle: bytes, name: str, denoise: bool = False) -> dict:
    response = _http().post(f"{_url(server)}/vr/push", files={"bundle": ("bundle.npz", bundle)},
                             data={"name": name, "denoise": str(denoise).lower()}, timeout=600)
    result = response.json()
    if response.status_code != 200:
        raise RuntimeError(result.get("error", response.text))
    return result


def fetch_labels(server: str, name: str) -> tuple[np.ndarray, np.ndarray, int]:
    # neueste maske als (z,y,x) in originalgroesse, dazu scale und version
    response = _http().get(f"{_url(server)}/vr/labels", params={"name": name}, timeout=120)
    if response.status_code != 200:
        raise RuntimeError(response.json().get("error", response.text))
    data = np.load(io.BytesIO(response.content))
    return data["labels"], data["scale"], int(data["version"])


def setup_usb(server: str) -> None:
    # server richtet adb reverse ein, danach erreicht die brille ihn uebers kabel
    response = _http().post(f"{_url(server)}/vr/usb", timeout=30)
    if response.status_code != 200:
        raise RuntimeError(response.json().get("error", response.text))


def fetch_view(server: str) -> dict:
    response = _http().get(f"{_url(server)}/vr/view", timeout=2)
    response.raise_for_status()
    return response.json()


def headset_active(server: str) -> bool | None:
    # zeigt die brille gerade ein bild, None wenn der server nicht antwortet
    try:
        return vr_is_active(fetch_view(server))
    except requests.RequestException:
        return None


def can_start_server(server: str) -> bool:
    # nur der server auf diesem rechner laesst sich von hier starten, und der hoert fest auf 8000
    url = urlparse(_url(server))
    return url.hostname == "127.0.0.1" and (url.port or 80) == SERVER_PORT and SERVER_SCRIPT.is_file()


def _server_pythons() -> list[str]:
    # server pythons, erst die eigene umgebung, dann das python aus dem sie gebaut ist
    base = Path(sys.base_prefix) / ("python.exe" if os.name == "nt" else "bin/python3")
    candidates = [os.environ.get("BIOIMAGE_VR_SERVER_PYTHON"), sys.executable, str(base)]
    found = []
    for candidate in candidates:
        if candidate and Path(candidate).is_file() and candidate not in found:
            found.append(candidate)
    return found


def start_server(server: str, wait_s: float = 90.0) -> None:
    # upload_server.py im hintergrund starten und warten bis er antwortet, log in SERVER_LOG
    for python in _server_pythons():
        with open(SERVER_LOG, "ab") as log:
            process = subprocess.Popen(
                [python, str(SERVER_SCRIPT)], cwd=SERVER_SCRIPT.parent, stdin=subprocess.DEVNULL,
                stdout=log, stderr=subprocess.STDOUT, env={**os.environ, "PYTHONUTF8": "1"},
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        deadline = time.monotonic() + wait_s
        # beendet sich der prozess gleich wieder, fehlen dort die pakete, dann das naechste python
        while process.poll() is None and time.monotonic() < deadline:
            if headset_active(server) is not None:
                atexit.register(process.terminate)
                return
            time.sleep(1)
        if process.poll() is None:
            process.terminate()
    raise RuntimeError(f"Server startet nicht, Ausgabe in {SERVER_LOG}")


def fetch_current(server: str, name: str) -> dict:
    # bild das die brille gerade zeigt, vom pc geholt: image (c,z,y,x), scale, evtl. labels und namen
    response = _http().get(f"{_url(server)}/vr/current", params={"name": name}, timeout=120)
    if response.status_code != 200:
        raise RuntimeError(response.json().get("error", response.text))
    data = np.load(io.BytesIO(response.content))
    return {key: data[key] for key in data.files}


def post_napari_view(server: str, view: dict) -> None:
    _http().post(f"{_url(server)}/vr/napari_view", json=view, timeout=2).raise_for_status()


def camera_to_view(view_direction, up_direction, shape_zyx, scale_zyx) -> dict:
    # gegenstueck zu view_to_camera, nur die richtung der napari kamera im volumen raum
    extent = np.asarray(shape_zyx, dtype=np.float64) * np.asarray(scale_zyx, dtype=np.float64)

    def to_cube(direction) -> dict:
        v = np.asarray(direction, dtype=np.float64) / extent
        v /= np.linalg.norm(v)
        return {"x": float(v[2]), "y": float(v[1]), "z": float(v[0])}

    return {"camForward": to_cube(view_direction), "camUp": to_cube(up_direction)}


def post_labels_patch(server: str, name: str, origin_zyx, block: np.ndarray, shape_zyx) -> None:
    # geaenderten ausschnitt der maske an die brille, block (z,y,x) ab origin in der maske der groesse shape
    params = {"name": name}
    params.update(zip("zyx", (int(v) for v in origin_zyx)))
    params.update(zip("dhw", (int(v) for v in block.shape)))
    params.update(zip(("sz", "sy", "sx"), (int(v) for v in shape_zyx)))
    _http().post(f"{_url(server)}/vr/labels_patch", params=params, data=block.astype("<u2").tobytes(),
                 headers={"Content-Type": "application/octet-stream"}, timeout=10).raise_for_status()


def fetch_pointer(server: str, after: int, wait_s: float = 1.0) -> dict:
    # was der controller strahl auf der leinwand getan hat, seq und events. after unter 0 liefert nur den stand
    response = _http().get(f"{_url(server)}/vr/pointer", params={"after": after, "wait": wait_s}, timeout=wait_s + 3)
    response.raise_for_status()
    return response.json()


def post_screen(server: str, jpeg: bytes) -> None:
    _http().post(f"{_url(server)}/vr/screen", data=jpeg, headers={"Content-Type": "image/jpeg"},
                 timeout=3).raise_for_status()


def ray_to_cube(start, end, shape_zyx) -> dict:
    # strahl unter dem mauszeiger aus layer koordinaten in den volumen raum, wuerfel von -0.5 bis 0.5
    shape = np.asarray(shape_zyx, dtype=np.float64)

    def to_cube(point) -> dict:
        # napari rechnet den strahl gegen den kasten von 0 bis groesse
        c = np.asarray(point, dtype=np.float64)[-3:] / shape - 0.5
        return {"x": float(c[2]), "y": float(c[1]), "z": float(c[0])}

    return {"rayStart": to_cube(start), "rayEnd": to_cube(end)}


def vr_is_active(view: dict, max_age_s: float = 1.5) -> bool:
    # die brille schickt nur solange sie getragen wird, abgesetzt schlaeft die app ein
    return bool(view) and view.get("age_s") is not None and view["age_s"] <= max_age_s


@dataclass
class CameraState:
    center: tuple[float, float, float]
    view_direction: tuple[float, float, float]
    up_direction: tuple[float, float, float]
    # None heisst zoom in napari lassen wie er ist
    zoom: float | None


def _vec(d: dict) -> np.ndarray:
    # unity schickt x,y,z, napari rechnet in z,y,x
    return np.array([d["z"], d["y"], d["x"]], dtype=np.float64)


def view_to_camera(view: dict, shape_zyx, scale_zyx, translate_zyx,
                   canvas_px: float, fov_deg: float, object_centric: bool = False) -> CameraState:
    # blick der brille als napari kamera, volumen raum ist der wuerfel von -0.5 bis 0.5
    shape = np.asarray(shape_zyx, dtype=np.float64)
    scale = np.asarray(scale_zyx, dtype=np.float64)
    translate = np.asarray(translate_zyx, dtype=np.float64)
    extent = shape * scale

    cube = _vec(view["camPos"])
    position = translate + ((cube + 0.5) * shape - 0.5) * scale
    volume_center = translate + (shape - 1) / 2 * scale

    # von aussen zaehlt der standort, nicht die kopfrichtung, objekt bleibt mittig
    if object_centric and np.max(np.abs(cube)) > 0.5:
        forward = volume_center - position
        forward /= np.linalg.norm(forward)
        up = _vec(view["camUp"]) * extent
        up -= forward * np.dot(up, forward)
        if np.linalg.norm(up) < 1e-6:
            up = np.array([0.0, 1.0, 0.0]) - forward * forward[1]
        up /= np.linalg.norm(up)
        return CameraState(tuple(volume_center), tuple(forward), tuple(up), None)

    forward = _vec(view["camForward"]) * extent
    forward /= np.linalg.norm(forward)
    up = _vec(view["camUp"]) * extent
    up -= forward * np.dot(up, forward)
    up /= np.linalg.norm(up)

    # napari dreht um einen mittelpunkt, der naechste punkt zur volumenmitte auf dem blickstrahl
    distance = max(float(np.dot(volume_center - position, forward)), 0.05 * float(np.linalg.norm(extent)))
    center = position + forward * distance

    # abstand ueber den zoom nachbilden, sichtbare breite = 2 * abstand * tan(fov / 2)
    zoom = canvas_px / (2 * distance * math.tan(math.radians(fov_deg) / 2))
    return CameraState(tuple(center), tuple(forward), tuple(up), zoom)


def describe_view(view: dict) -> str:
    if not view:
        return "Noch kein Blick aus der Brille empfangen."
    parts = [f"VR: {view.get('name') or '?'}" if vr_is_active(view) else "Brille ist abgesetzt, napari fuehrt"]
    if view.get("selected"):
        parts.append(f"Zelle {view['selected']} ausgewaehlt")
    if view.get("hover"):
        parts.append(f"Zelle {view['hover']} unter dem Strahl")
    age = view.get("age_s")
    if age is not None and age > 2:
        parts.append(f"letztes Signal vor {age:.0f} s")
    return " · ".join(parts)
