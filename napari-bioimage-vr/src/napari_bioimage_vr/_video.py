"""live video aus der brille, variante A.

die quest gibt ihr display per adb screenrecord als h264 strom aus, hier dekodiert.
braucht usb kabel (oder adb ueber wlan), die app auf der brille muss nichts tun.
das display liefert beide augen nebeneinander, 4128x2208 auf der quest 3, wir schneiden aus.
schwarz solange die brille nicht getragen wird, dann gibt die quest kein bild aus.
bei stehendem bild schickt die quest kaum neue frames, die rate steigt sobald sich was bewegt
"""

from __future__ import annotations

import glob
import shutil
import subprocess
import threading
import time

import numpy as np

# opencv fuer die entzerrung, ein paar ms pro bild statt mehrerer hundert in numpy
try:
    import cv2 as _cv2
except ImportError:
    _cv2 = None

# vielfache von 16, sonst lehnt der encoder ab, seitenverhaeltnis wie das quest 3 display
STREAM_SIZE = (1920, 1024)
BIT_RATE = "8M"
MAX_FPS = 30


def find_adb() -> str:
    """adb suchen, erst path dann unity bundle, gleiche logik wie deploy_to_headset.py"""
    on_path = shutil.which("adb")
    if on_path:
        return on_path
    pattern = r"C:\Program Files\Unity\Hub\Editor\*\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
    matches = glob.glob(pattern)
    if matches:
        return matches[0]
    raise FileNotFoundError("adb nicht gefunden, weder im PATH noch in einer Unity-Installation")


def crop_eye(frame: np.ndarray, eye: str) -> np.ndarray:
    """links, rechts oder beide augen"""
    if eye == "beide":
        return frame
    half = frame.shape[1] // 2
    return frame[:, :half] if eye == "links" else frame[:, half:]


class Flattener:
    """macht aus einem linsenverzerrten augenbild ein flaches bild.

    das display bekommt die bilder so vorverzerrt, dass sie durch die linse gerade wirken.
    hier radial zurueckgerechnet, r_quelle = r * (1 + strength * r^2) / zoom, r normiert
    auf die halbe bildbreite. genaues linsenmodell der quest ist nicht bekannt, deshalb
    regler im plugin statt fester werte. tabellen werden nur bei neuer groesse neu gebaut"""

    def __init__(self, strength: float = 0.25, zoom: float = 1.3, center_x: float = 0.0,
                 rotation_deg: float = 10.0):
        self.strength = strength
        self.zoom = zoom
        self.center_x = center_x
        # positiv dreht das ergebnis im uhrzeigersinn, 29.09. eric: ca 10 grad noetig
        self.rotation_deg = rotation_deg
        self._key = None
        self._maps = None
        self._right: Flattener | None = None

    def right(self) -> "Flattener":
        """fuers rechte auge, linsen sitzen spiegelbildlich zur nase. einmal gebaut und
        gemerkt, sonst waeren die tabellen bei jedem bild neu zu rechnen"""
        if self._right is None:
            self._right = Flattener(self.strength, self.zoom, -self.center_x, -self.rotation_deg)
        return self._right

    def _build(self, height: int, width: int):
        cx = (width - 1) / 2 * (1 + self.center_x)
        cy = (height - 1) / 2
        half = max(width, height) / 2
        ys, xs = np.mgrid[0:height, 0:width].astype(np.float32)
        u = (xs - (width - 1) / 2) / half
        v = (ys - cy) / half
        # ausgabe im uhrzeigersinn drehen heisst quelle gegen den uhrzeigersinn abtasten,
        # bildkoordinaten mit y nach unten
        angle = np.radians(self.rotation_deg)
        u, v = u * np.cos(angle) + v * np.sin(angle), -u * np.sin(angle) + v * np.cos(angle)
        factor = (1 + self.strength * (u * u + v * v)) / self.zoom
        src_x = np.clip(cx + u * factor * half, 0, width - 1).astype(np.float32)
        src_y = np.clip(cy + v * factor * half, 0, height - 1).astype(np.float32)
        if _cv2 is not None:
            return _cv2.convertMaps(src_x, src_y, _cv2.CV_16SC2)
        # ohne opencv nearest neighbor ueber einen flachen index, bilinear in numpy waere zu langsam
        return (np.rint(src_y).astype(np.int64) * width + np.rint(src_x).astype(np.int64),)

    def apply(self, frame: np.ndarray) -> np.ndarray:
        height, width = frame.shape[:2]
        key = (height, width, self.strength, self.zoom, self.center_x, self.rotation_deg)
        if key != self._key:
            self._maps = self._build(height, width)
            self._key = key
        if _cv2 is not None:
            return _cv2.remap(frame, self._maps[0], self._maps[1], _cv2.INTER_LINEAR)
        flat = frame.reshape(-1, frame.shape[2])
        return flat[self._maps[0]]


def flatten_eyes(frame: np.ndarray, eye: str, flattener: Flattener) -> np.ndarray:
    """bei beiden augen jedes fuer sich entzerren, sonst waere die mitte die naht.
    rechtes auge gespiegelt, siehe Flattener.right"""
    if eye == "links":
        return flattener.apply(frame)
    if eye == "rechts":
        return flattener.right().apply(frame)
    half = frame.shape[1] // 2
    return np.concatenate([flattener.apply(frame[:, :half]), flattener.right().apply(frame[:, half:])], axis=1)


def explain_error(message: str) -> str:
    if "INVALID_LAYER_STACK" in message:
        return "Die Brille ist im Standby. Aufsetzen oder kurz den Einschaltknopf druecken, dann Video neu starten."
    return message.strip()


class VideoStream:
    """screenrecord auf der brille, dekodiert zu rgb bildern.

    frames() ist ein generator fuer einen hintergrund thread, stop() kommt vom ui thread
    und beendet sofort, auch wenn gerade kein bild kommt (stehendes oder schwarzes bild).
    fehler kommen als str statt exception, napari verwirft exceptions aus workern sonst teils"""

    def __init__(self, eye: str = "links", adb: str | None = None):
        self.eye = eye
        self.adb = adb or find_adb()
        self._stopped = threading.Event()
        self._proc: subprocess.Popen | None = None
        self._remote_pid: str | None = None
        # None heisst rohes augenbild, das ui tauscht das objekt live aus
        self.flattener: Flattener | None = None

    def frames(self):
        import av

        width, height = STREAM_SIZE
        # erste zeile ist die pid auf der brille, danach kommt der h264 strom
        remote = (f"echo $$; exec screenrecord --output-format=h264 --size {width}x{height} "
                  f"--bit-rate {BIT_RATE} --time-limit 0 -")
        min_interval = 1.0 / MAX_FPS
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)

        while not self._stopped.is_set():
            self._proc = subprocess.Popen([self.adb, "exec-out", "sh", "-c", remote],
                                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                          creationflags=creationflags)
            self._remote_pid = self._proc.stdout.readline().decode(errors="ignore").strip() or None
            codec = av.CodecContext.create("h264", "r")
            last_yield = 0.0
            first = True
            try:
                while not self._stopped.is_set():
                    # read1 liefert was da ist, read wuerde auf volle 16 kb warten
                    chunk = self._proc.stdout.read1(16384)
                    if not chunk:
                        break
                    # fehler von screenrecord kommen bei exec-out im selben strom statt auf stderr
                    if first and chunk.startswith(b"ERROR"):
                        yield explain_error(chunk.decode(errors="ignore"))
                        return
                    first = False
                    for packet in codec.parse(chunk):
                        for frame in codec.decode(packet):
                            now = time.monotonic()
                            if now - last_yield < min_interval:
                                continue
                            last_yield = now
                            image = crop_eye(frame.to_ndarray(format="rgb24"), self.eye)
                            flattener = self.flattener
                            if flattener is not None:
                                image = flatten_eyes(image, self.eye, flattener)
                            yield image
            finally:
                self._kill()

            if self._stopped.is_set():
                return
            error = self._proc.stderr.read().decode(errors="ignore").strip()
            if error:
                yield explain_error(error)
                return
            # kurz warten, sonst dreht die schleife durch wenn gar kein geraet dran ist
            time.sleep(1)

    def stop(self) -> None:
        self._stopped.set()
        self._kill()

    def _kill(self) -> None:
        # adb auf dem pc zu beenden reicht nicht, screenrecord laeuft auf der brille sonst weiter
        if self._remote_pid and self._remote_pid.isdigit():
            subprocess.run([self.adb, "shell", "kill", "-INT", self._remote_pid], capture_output=True,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self._remote_pid = None
        if self._proc is not None and self._proc.poll() is None:
            self._proc.kill()
