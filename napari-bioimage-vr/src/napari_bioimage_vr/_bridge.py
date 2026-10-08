"""logik ohne napari und qt, einzeln testbar.

gegenstueck zu preprocessing/vr_bridge.py auf dem server und VrBridge.cs in der app
"""

from __future__ import annotations

import io
import math
from dataclasses import dataclass

import numpy as np
import requests

MAX_CHANNELS = 3


def to_czyx(array: np.ndarray, rgb: bool = False) -> np.ndarray:
    """ein layer als (c,z,y,x). rgb kanaele liegen hinten, 2d wird zu z=1,
    mehr als 4 achsen (z.b. zeit) auf den ersten index reduziert"""
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
    """npz fuer POST /vr/push, kanaele muessen dieselbe raeumliche groesse haben"""
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
    response = requests.post(f"{server}/vr/push", files={"bundle": ("bundle.npz", bundle)},
                             data={"name": name, "denoise": str(denoise).lower()}, timeout=600)
    result = response.json()
    if response.status_code != 200:
        raise RuntimeError(result.get("error", response.text))
    return result


def fetch_labels(server: str, name: str) -> tuple[np.ndarray, np.ndarray, int]:
    """neueste maske als (z,y,x) in originalgroesse, dazu scale und version"""
    response = requests.get(f"{server}/vr/labels", params={"name": name}, timeout=120)
    if response.status_code != 200:
        raise RuntimeError(response.json().get("error", response.text))
    data = np.load(io.BytesIO(response.content))
    return data["labels"], data["scale"], int(data["version"])


def setup_usb(server: str) -> None:
    """server richtet adb reverse ein, danach erreicht die brille ihn uebers kabel"""
    response = requests.post(f"{server}/vr/usb", timeout=30)
    if response.status_code != 200:
        raise RuntimeError(response.json().get("error", response.text))


def fetch_view(server: str) -> dict:
    response = requests.get(f"{server}/vr/view", timeout=2)
    response.raise_for_status()
    return response.json()


@dataclass
class CameraState:
    center: tuple[float, float, float]
    view_direction: tuple[float, float, float]
    up_direction: tuple[float, float, float]
    zoom: float


def _vec(d: dict) -> np.ndarray:
    # unity schickt x,y,z, napari rechnet in z,y,x
    return np.array([d["z"], d["y"], d["x"]], dtype=np.float64)


def view_to_camera(view: dict, shape_zyx, scale_zyx, translate_zyx,
                   canvas_px: float, fov_deg: float) -> CameraState:
    """blick aus der brille auf eine napari kamera fuer denselben layer.

    die app schickt alles im volumen raum, wuerfel von -0.5 bis 0.5 je achse. das ist
    unabhaengig von zoom und drehung in vr und von einer verkleinerung beim senden.
    uvw 0 und 1 sind die aeusseren voxelkanten, napari setzt voxel i auf i * scale"""
    shape = np.asarray(shape_zyx, dtype=np.float64)
    scale = np.asarray(scale_zyx, dtype=np.float64)
    translate = np.asarray(translate_zyx, dtype=np.float64)
    extent = shape * scale

    position = translate + ((_vec(view["camPos"]) + 0.5) * shape - 0.5) * scale
    forward = _vec(view["camForward"]) * extent
    forward /= np.linalg.norm(forward)
    up = _vec(view["camUp"]) * extent
    up -= forward * np.dot(up, forward)
    up /= np.linalg.norm(up)

    # napari dreht um einen mittelpunkt, der naechste punkt zur volumenmitte auf dem blickstrahl
    volume_center = translate + (shape - 1) / 2 * scale
    distance = max(float(np.dot(volume_center - position, forward)), 0.05 * float(np.linalg.norm(extent)))
    center = position + forward * distance

    # abstand ueber den zoom nachbilden, sichtbare breite = 2 * abstand * tan(fov / 2)
    zoom = canvas_px / (2 * distance * math.tan(math.radians(fov_deg) / 2))
    return CameraState(tuple(center), tuple(forward), tuple(up), zoom)


def describe_view(view: dict) -> str:
    if not view:
        return "Noch kein Blick aus der Brille empfangen."
    parts = [f"VR: {view.get('name') or '?'}"]
    if view.get("selected"):
        parts.append(f"Zelle {view['selected']} ausgewaehlt")
    if view.get("hover"):
        parts.append(f"Zelle {view['hover']} unter dem Strahl")
    age = view.get("age_s")
    if age is not None and age > 2:
        parts.append(f"letztes Signal vor {age:.0f} s")
    return " · ".join(parts)
