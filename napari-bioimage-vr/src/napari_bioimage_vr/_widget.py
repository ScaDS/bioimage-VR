"""seitenfenster in napari, Plugins > Bioimage VR"""

from __future__ import annotations

import time

import napari
import numpy as np
from napari.layers import Image, Labels
from napari.qt.threading import thread_worker
from qtpy.QtCore import Qt
from qtpy.QtGui import QImage, QPixmap
from qtpy.QtWidgets import (
    QAbstractItemView,
    QCheckBox,
    QComboBox,
    QFormLayout,
    QGroupBox,
    QLabel,
    QLineEdit,
    QListWidget,
    QPushButton,
    QSizePolicy,
    QSlider,
    QVBoxLayout,
    QWidget,
)

from . import _bridge, _video

NO_LABELS = "(keine Maske)"
# sichtfeld der napari kamera im live modus, ungefaehr wie in der quest
LIVE_FOV_DEG = 60.0


class BioimageVRWidget(QWidget):
    def __init__(self, napari_viewer: napari.viewer.Viewer):
        super().__init__()
        self.viewer = napari_viewer
        self._live_worker = None
        self._last_selected = 0

        self.server_edit = QLineEdit("http://localhost:8000")
        self.usb_button = QPushButton("Brille per USB verbinden")
        self.usb_button.setToolTip("Fuer Netze, in denen Brille und PC sich nicht sehen, z.B. eduroam. "
                                   "Nach jedem neuen Einstecken nochmal druecken.")
        self.image_list = QListWidget()
        self.image_list.setSelectionMode(QAbstractItemView.MultiSelection)
        self.image_list.setMaximumHeight(110)
        self.labels_combo = QComboBox()
        self.name_edit = QLineEdit()
        self.denoise_check = QCheckBox("Entrauschen (Median 3x3x3)")
        self.to_vr_button = QPushButton("To VR")
        self.from_vr_button = QPushButton("From VR")
        self.live_check = QCheckBox("Blick aus der Brille live spiegeln")
        self.highlight_check = QCheckBox("Nur die in VR ausgewaehlte Zelle zeigen")
        self.video_check = QCheckBox("Video aus der Brille zeigen (USB)")
        self.eye_combo = QComboBox()
        self.eye_combo.addItems(["links", "rechts", "beide"])
        self.snapshot_button = QPushButton("Standbild als Layer")
        self.snapshot_button.setEnabled(False)
        # flaches bild statt linsenverzerrtem augenbild, werte live einstellbar
        self.flat_check = QCheckBox("Flach entzerren")
        self.flat_check.setChecked(True)
        self.strength_slider = _slider(-60, 60, 25)
        self.zoom_slider = _slider(100, 250, 130)
        self.center_slider = _slider(-25, 25, 0)
        # grad, positiv im uhrzeigersinn, rechtes auge wird gespiegelt
        self.rotation_slider = _slider(-30, 30, 10)
        self.flat_values = QLabel("")
        self.status_label = QLabel("")
        self.status_label.setWordWrap(True)

        send_box = QGroupBox("An die Brille")
        send_form = QFormLayout(send_box)
        send_form.addRow("Kanaele (max. 3)", self.image_list)
        send_form.addRow("Maske", self.labels_combo)
        send_form.addRow("Name", self.name_edit)
        send_form.addRow(self.denoise_check)
        send_form.addRow(self.to_vr_button)

        back_box = QGroupBox("Aus der Brille")
        back_layout = QVBoxLayout(back_box)
        back_layout.addWidget(self.from_vr_button)
        back_layout.addWidget(self.live_check)
        back_layout.addWidget(self.highlight_check)

        video_box = QGroupBox("Video aus der Brille")
        video_form = QFormLayout(video_box)
        video_form.addRow(self.video_check)
        video_form.addRow("Auge", self.eye_combo)
        video_form.addRow(self.flat_check)
        video_form.addRow("Entzerrung", self.strength_slider)
        video_form.addRow("Zoom", self.zoom_slider)
        video_form.addRow("Mitte", self.center_slider)
        video_form.addRow("Drehung", self.rotation_slider)
        video_form.addRow(self.flat_values)
        video_form.addRow(self.snapshot_button)

        layout = QVBoxLayout(self)
        server_form = QFormLayout()
        server_form.addRow("Server", self.server_edit)
        server_form.addRow(self.usb_button)
        layout.addLayout(server_form)
        layout.addWidget(send_box)
        layout.addWidget(back_box)
        layout.addWidget(video_box)
        layout.addWidget(self.status_label)
        layout.addStretch()

        self.usb_button.clicked.connect(self._setup_usb)
        self.to_vr_button.clicked.connect(self._to_vr)
        self.from_vr_button.clicked.connect(self._from_vr)
        self.live_check.toggled.connect(self._toggle_live)
        self.video_check.toggled.connect(self._toggle_video)
        self.snapshot_button.clicked.connect(self._snapshot)
        self.flat_check.toggled.connect(self._update_flattener)
        for slider in (self.strength_slider, self.zoom_slider, self.center_slider, self.rotation_slider):
            slider.valueChanged.connect(self._update_flattener)
        self._video_worker = None
        self._video_stream = None
        self._video_panel = None
        self._last_frame = None
        self._black_since = None
        self._update_flattener()
        self.image_list.itemSelectionChanged.connect(self._suggest_name)
        self.viewer.layers.events.inserted.connect(self._refresh_layers)
        self.viewer.layers.events.removed.connect(self._refresh_layers)
        self._refresh_layers()

    @property
    def server(self) -> str:
        return self.server_edit.text().strip().rstrip("/")

    def _status(self, text: str) -> None:
        self.status_label.setText(text)

    # usb

    def _setup_usb(self) -> None:
        self.usb_button.setEnabled(False)
        self._status("Richte USB-Verbindung ein ...")
        worker = thread_worker(_bridge.setup_usb)(self.server)
        worker.returned.connect(lambda _: self._status(
            "Brille per USB verbunden. Die App findet den PC jetzt auch ohne gemeinsames WLAN."))
        worker.errored.connect(lambda error: self._status(f"USB-Verbindung fehlgeschlagen: {error}"))
        worker.finished.connect(lambda: self.usb_button.setEnabled(True))
        worker.start()

    # layer auswahl

    def _refresh_layers(self, event=None) -> None:
        selected = {item.text() for item in self.image_list.selectedItems()}
        self.image_list.clear()
        for layer in self.viewer.layers:
            if isinstance(layer, Image):
                self.image_list.addItem(layer.name)
                item = self.image_list.item(self.image_list.count() - 1)
                item.setSelected(layer.name in selected or (not selected and self.image_list.count() == 1))

        current = self.labels_combo.currentText()
        self.labels_combo.clear()
        self.labels_combo.addItem(NO_LABELS)
        for layer in self.viewer.layers:
            if isinstance(layer, Labels):
                self.labels_combo.addItem(layer.name)
        index = self.labels_combo.findText(current)
        # neue maske gleich vorauswaehlen, das ist der haeufigste fall
        self.labels_combo.setCurrentIndex(index if index > 0 else self.labels_combo.count() - 1)
        self._suggest_name()

    def _suggest_name(self) -> None:
        items = self.image_list.selectedItems()
        if items and not self.name_edit.isModified():
            self.name_edit.setText(items[0].text())

    def _selected_images(self) -> list[Image]:
        names = [item.text() for item in self.image_list.selectedItems()]
        return [self.viewer.layers[name] for name in names]

    def _selected_labels(self) -> Labels | None:
        name = self.labels_combo.currentText()
        return None if name == NO_LABELS else self.viewer.layers[name]

    def _reference_layer(self):
        """layer fuer die umrechnung des blicks, bild vor maske"""
        images = self._selected_images()
        if images:
            return images[0]
        return self._selected_labels()

    # to vr

    def _to_vr(self) -> None:
        images = self._selected_images()
        if not images:
            self._status("Bitte mindestens einen Bild-Layer auswaehlen.")
            return
        if len(images) > _bridge.MAX_CHANNELS:
            self._status(f"Hoechstens {_bridge.MAX_CHANNELS} Kanaele, die ersten werden genommen.")

        labels_layer = self._selected_labels()
        try:
            channels = [_bridge.to_czyx(_layer_data(layer), rgb=layer.rgb) for layer in images]
            labels = _layer_data(labels_layer) if labels_layer is not None else None
            scale = tuple(float(s) for s in images[0].scale[-3:])
            if len(scale) < 3:
                scale = (1.0,) * (3 - len(scale)) + scale
            bundle = _bridge.build_bundle(channels, labels, scale, [layer.name for layer in images])
        except Exception as error:
            self._status(f"Konnte nicht vorbereitet werden: {error}")
            return

        name = self.name_edit.text().strip() or images[0].name
        self.to_vr_button.setEnabled(False)
        self._status(f"Sende '{name}' an die Brille ...")

        worker = thread_worker(_bridge.push)(self.server, bundle, name, self.denoise_check.isChecked())
        worker.returned.connect(self._pushed)
        worker.errored.connect(lambda error: self._status(f"Senden fehlgeschlagen: {error}"))
        worker.finished.connect(lambda: self.to_vr_button.setEnabled(True))
        worker.start()

    def _pushed(self, result: dict) -> None:
        self.name_edit.setText(result["name"])
        note = " (fuer die Brille verkleinert)" if result.get("downsampled") else ""
        self._status(f"'{result['name']}' ist unterwegs, die Brille oeffnet es in wenigen Sekunden{note}.")

    # from vr

    def _from_vr(self) -> None:
        name = self.name_edit.text().strip()
        if not name:
            self._status("Name des Bilds auf der Brille eintragen.")
            return
        self.from_vr_button.setEnabled(False)
        worker = thread_worker(_bridge.fetch_labels)(self.server, name)
        worker.returned.connect(lambda result: self._show_labels(name, *result))
        worker.errored.connect(lambda error: self._status(f"Holen fehlgeschlagen: {error}"))
        worker.finished.connect(lambda: self.from_vr_button.setEnabled(True))
        worker.start()

    def _show_labels(self, name: str, labels: np.ndarray, scale: np.ndarray, version: int) -> None:
        layer_name = f"{name} (VR)"
        reference = self._reference_layer()
        translate = tuple(reference.translate[-3:]) if reference is not None else (0.0, 0.0, 0.0)
        if layer_name in self.viewer.layers:
            layer = self.viewer.layers[layer_name]
            layer.data = labels
        else:
            layer = self.viewer.add_labels(labels, name=layer_name, scale=tuple(scale), translate=translate)
        self._status(f"Maske Version {version} aus der Brille geladen als '{layer.name}'.")

    # live ansicht

    def _toggle_live(self, on: bool) -> None:
        if not on:
            if self._live_worker is not None:
                self._live_worker.quit()
                self._live_worker = None
            self._status("Live-Ansicht aus.")
            return

        self.viewer.dims.ndisplay = 3
        _camera(self.viewer).perspective = LIVE_FOV_DEG
        self._live_worker = _poll_view(self.server)
        self._live_worker.yielded.connect(self._apply_view)
        self._live_worker.start()

    def _apply_view(self, view: dict) -> None:
        self._status(_bridge.describe_view(view))
        reference = self._reference_layer()
        if not view or "camPos" not in view or reference is None:
            return

        shape = _layer_shape(reference)
        state = _bridge.view_to_camera(view, shape, reference.scale[-3:], reference.translate[-3:],
                                       _canvas_size(self.viewer), LIVE_FOV_DEG)
        camera = _camera(self.viewer)
        camera.center = state.center
        camera.set_view_direction(state.view_direction, up_direction=state.up_direction)
        camera.zoom = state.zoom

        self._apply_selection(int(view.get("selected") or 0))

    def _apply_selection(self, selected: int) -> None:
        labels_layer = self._selected_labels()
        if labels_layer is None:
            return
        if selected and selected != self._last_selected:
            labels_layer.selected_label = selected
        labels_layer.show_selected_label = bool(selected) and self.highlight_check.isChecked()
        self._last_selected = selected

    # video, variante A

    def _toggle_video(self, on: bool) -> None:
        if not on:
            if self._video_worker is not None:
                self._video_stream.stop()
                self._video_worker.quit()
                self._video_worker = None
            self.snapshot_button.setEnabled(False)
            self._status("Video aus.")
            return

        if self._video_panel is None:
            self._video_panel = VideoPanel()
            self.viewer.window.add_dock_widget(self._video_panel, name="VR-Video", area="right")
        self._black_since = None
        self._status("Starte Video, das erste Bild kommt nach etwa einer Sekunde ...")
        try:
            self._video_stream = _video.VideoStream(self.eye_combo.currentText())
        except FileNotFoundError as error:
            self.video_check.setChecked(False)
            self._status(str(error))
            return
        self._update_flattener()
        worker = thread_worker(self._video_stream.frames)()
        worker.yielded.connect(self._show_frame)
        worker.errored.connect(self._video_failed)
        self._video_worker = worker
        worker.start()

    def _update_flattener(self, *_) -> None:
        strength = self.strength_slider.value() / 100
        zoom = self.zoom_slider.value() / 100
        center = self.center_slider.value() / 100
        rotation = self.rotation_slider.value()
        on = self.flat_check.isChecked()
        for slider in (self.strength_slider, self.zoom_slider, self.center_slider, self.rotation_slider):
            slider.setEnabled(on)
        self.flat_values.setText(
            f"Entzerrung {strength:+.2f} · Zoom {zoom:.2f} · Mitte {center:+.2f} · Drehung {rotation:+d}°" if on else "")
        # neues objekt statt felder aendern, der video thread sieht so nie einen halben zustand
        if self._video_stream is not None:
            self._video_stream.flattener = _video.Flattener(strength, zoom, center, rotation) if on else None

    def _video_failed(self, error) -> None:
        self._video_worker = None
        self.video_check.setChecked(False)
        self._status(f"Video fehlgeschlagen: {error}")

    def _show_frame(self, frame) -> None:
        if isinstance(frame, str):
            self._video_failed(frame)
            return
        self._last_frame = frame
        self.snapshot_button.setEnabled(True)
        if self._video_panel is not None:
            self._video_panel.show_frame(frame)

        # die quest gibt nur bild aus, solange sie getragen wird
        if frame.mean() < 1.0:
            self._black_since = self._black_since or time.monotonic()
            if time.monotonic() - self._black_since > 2:
                self._status("Das Bild ist schwarz. Die Quest zeigt nur etwas, solange sie getragen wird.")
        elif self._black_since is not None:
            self._black_since = None
            self._status("Video laeuft.")

    def _snapshot(self) -> None:
        if self._last_frame is None:
            return
        layer = self.viewer.add_image(self._last_frame.copy(), name="VR-Standbild", rgb=True)
        self._status(f"Standbild als '{layer.name}' uebernommen.")

    def closeEvent(self, event) -> None:
        self.live_check.setChecked(False)
        self.video_check.setChecked(False)
        super().closeEvent(event)


def _slider(minimum: int, maximum: int, value: int) -> QSlider:
    slider = QSlider(Qt.Horizontal)
    slider.setRange(minimum, maximum)
    slider.setValue(value)
    return slider


class VideoPanel(QLabel):
    """zeigt die bilder aus der brille, skaliert ins fenster ohne zu verzerren"""

    def __init__(self):
        super().__init__("Warte auf Video ...")
        self.setAlignment(Qt.AlignCenter)
        self.setMinimumSize(320, 240)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self._image = None

    def show_frame(self, frame: np.ndarray) -> None:
        frame = np.ascontiguousarray(frame)
        height, width, _ = frame.shape
        # QImage haelt nur einen zeiger, das array muss so lange leben
        self._image = (frame, QImage(frame.data, width, height, 3 * width, QImage.Format_RGB888))
        pixmap = QPixmap.fromImage(self._image[1])
        self.setPixmap(pixmap.scaled(self.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation))


@thread_worker
def _poll_view(server: str):
    """blick etwa zehnmal pro sekunde, fehler nur melden statt abzubrechen"""
    while True:
        try:
            yield _bridge.fetch_view(server)
            time.sleep(0.1)
        except Exception:
            yield {}
            time.sleep(2)


def _layer_data(layer) -> np.ndarray:
    data = layer.data[0] if getattr(layer, "multiscale", False) else layer.data
    return np.asarray(data)


def _layer_shape(layer) -> tuple[int, int, int]:
    data = layer.data[0] if getattr(layer, "multiscale", False) else layer.data
    shape = data.shape[:-1] if getattr(layer, "rgb", False) else data.shape
    shape = tuple(shape[-3:])
    return (1,) * (3 - len(shape)) + shape


def _camera(viewer):
    # ab napari 0.9 viewer.scene.camera, davor viewer.camera
    scene = getattr(viewer, "scene", None)
    return scene.camera if scene is not None else viewer.camera


def _canvas_size(viewer) -> float:
    # ab napari 0.6 viewer.canvas.size, davor nur das private _canvas_size
    for getter in (lambda: viewer.canvas.size, lambda: viewer._canvas_size):
        try:
            size = getter()
            if size:
                return float(min(size))
        except Exception:
            pass
    return 800.0
