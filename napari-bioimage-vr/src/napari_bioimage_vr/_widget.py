"""seitenfenster in napari, Plugins > Bioimage VR"""

from __future__ import annotations

import threading
import time

import napari
import numpy as np
import pandas as pd
from napari.layers import Image, Labels
from napari.qt.threading import thread_worker
from qtpy.QtCore import Qt, QTimer
from qtpy.QtGui import QImage, QPixmap
from qtpy.QtWidgets import (
    QAbstractItemView,
    QCheckBox,
    QComboBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
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
# ab dieser groesse wird fuer die anzeige verkleinert
DISPLAY_VOXELS = 4_000_000
# wer die ansicht vorgibt, reihenfolge wie die knoepfe
LEAD_AUTO, LEAD_VR, LEAD_NAPARI = range(3)


class BioimageVRWidget(QWidget):
    def __init__(self, napari_viewer: napari.viewer.Viewer):
        super().__init__()
        self.viewer = napari_viewer
        self._live_worker = None
        self._last_selected = 0
        # name des bilds das gerade aus der brille nachgeladen wird bzw. wurde
        self._fetching = None
        self._followed = None
        self._vr_active = False
        self._last_sent = 0.0
        # neuester blick aus der brille, timer wendet nur den letzten an.
        # jeden einzeln abarbeiten staut sich, man sieht dann nur die endstellung
        self._latest_view = None
        self._applied_view = None
        # eigene kamera aenderungen von denen aus der brille unterscheiden
        self._applying = False
        self._napari_leads_until = 0.0
        self._camera_connected = False
        self._lead = LEAD_AUTO
        self._outbox = None
        self._outbox_ready = threading.Event()
        threading.Thread(target=self._send_loop, daemon=True).start()
        self._watched_labels = None
        self._highlighted = None
        self._view_timer = QTimer(self)
        self._view_timer.setInterval(40)
        self._view_timer.timeout.connect(self._apply_latest_view)
        # zoom der kamera beim letzten ereignis, daraus der faktor je schritt
        self._zoom_seen = None
        # alle eigenen zoom schritte aufmultipliziert, die brille wendet das verhaeltnis zum letzten stand an
        self._zoom_factor = 1.0
        # neu je fenster, die brille merkt daran dass der faktor wieder bei 1 anfaengt
        self._zoom_id = int(time.time() * 1000)
        # schickt den letzten stand nach, wenn das ereignis dazu in die drosselung fiel
        self._send_timer = QTimer(self)
        self._send_timer.setSingleShot(True)
        self._send_timer.setInterval(60)
        self._send_timer.timeout.connect(self._send_trailing)
        # schaut solange die kopplung aus ist nach, ob die brille ein bild zeigt, und schaltet sie dann ein
        self._presence_busy = False
        self._server_tried = False
        # von hand ausgeschaltet, dann bleibt es aus bis die brille einmal abgesetzt war
        self._auto_blocked = False
        self._presence_timer = QTimer(self)
        self._presence_timer.setInterval(2000)
        self._presence_timer.timeout.connect(self._check_presence)

        self.server_edit = QLineEdit("http://127.0.0.1:8000")
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
        self.live_check = QCheckBox("Mit der Brille synchron halten")
        self.live_check.setToolTip("Geht von selbst an, sobald die Brille ein Bild zeigt. "
                                   "Von Hand ausgeschaltet bleibt es aus, bis die Brille einmal abgesetzt war.")
        self.highlight_check = QCheckBox("Nur die in VR ausgewaehlte Zelle zeigen")
        # automatisch: getragen die brille, abgesetzt napari, drehen mit der maus uebernimmt kurz.
        # napari steuert: arbeiten am rechner mit aufgesetzter brille
        self.lead_buttons = []
        for index, text in enumerate(["Automatisch", "Brille steuert", "napari steuert"]):
            button = QPushButton(text)
            button.setCheckable(True)
            button.setMinimumHeight(34)
            button.setChecked(index == LEAD_AUTO)
            button.clicked.connect(lambda _=False, i=index: self._set_lead(i))
            self.lead_buttons.append(button)
        self.lead_buttons[LEAD_AUTO].setToolTip("Getragen steuert die Brille, abgesetzt napari. "
                                                "Wer in napari mit der Maus dreht oder zoomt, uebernimmt kurz.")
        self.lead_buttons[LEAD_VR].setToolTip("napari folgt nur, Drehen und Zoomen mit der Maus "
                                              "aendert in der Brille nichts.")
        self.lead_buttons[LEAD_NAPARI].setToolTip("Zum Arbeiten am Rechner mit aufgesetzter Brille: "
                                                  "Drehen, Zoomen mit dem Mausrad und Auswaehlen in napari "
                                                  "kommt in der Brille an.")
        self.lead_label = QLabel("Kopplung ist aus")
        self.lead_label.setStyleSheet("font-weight: bold;")
        self.fast_check = QCheckBox("Schnelle Anzeige (grosse Bilder verkleinert)")
        self.fast_check.setChecked(True)
        self.fast_check.setToolTip("Bilder aus der Brille werden fuer napari auf etwa 4 Mio. Voxel verkleinert, "
                                   "sonst kommt das Mitdrehen auf schwachen Grafikkarten nicht hinterher.")
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
        back_layout.addWidget(self.highlight_check)
        back_layout.addWidget(self.fast_check)

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
        sync_box = QGroupBox("Wer steuert die Ansicht")
        sync_layout = QVBoxLayout(sync_box)
        sync_layout.addWidget(self.live_check)
        lead_row = QHBoxLayout()
        for button in self.lead_buttons:
            lead_row.addWidget(button)
        sync_layout.addLayout(lead_row)
        sync_layout.addWidget(self.lead_label)
        layout.addWidget(sync_box)
        layout.addWidget(send_box)
        layout.addWidget(back_box)
        layout.addWidget(video_box)
        layout.addWidget(self.status_label)
        layout.addStretch()

        self.usb_button.clicked.connect(self._setup_usb)
        self.to_vr_button.clicked.connect(self._to_vr)
        self.from_vr_button.clicked.connect(self._from_vr)
        self.live_check.toggled.connect(self._toggle_live)
        # clicked kommt nur vom nutzer, nicht vom automatischen einschalten
        self.live_check.clicked.connect(self._live_clicked)
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
        self._presence_timer.start()
        self._check_presence()

    @property
    def server(self) -> str:
        return self.server_edit.text().strip().rstrip("/")

    def _status(self, text: str) -> None:
        self.status_label.setText(text)

    # von selbst verbinden

    def _check_presence(self) -> None:
        if self._live_worker is not None or self._presence_busy:
            return
        self._presence_busy = True
        worker = thread_worker(_bridge.headset_active)(self.server)
        worker.returned.connect(self._presence_known)
        worker.errored.connect(lambda error: self._presence_known(False))
        worker.start()

    def _presence_known(self, active) -> None:
        self._presence_busy = False
        if active is None:
            self._start_server()
        elif not active:
            self._auto_blocked = False
        elif not self._auto_blocked and self._live_worker is None:
            self.live_check.setChecked(True)

    def _start_server(self) -> None:
        """server laeuft nicht, den aus dem repo einmal selbst starten"""
        if self._server_tried or not _bridge.can_start_server(self.server):
            return
        self._server_tried = True
        self._presence_busy = True
        self._status("Server auf dem PC laeuft nicht, wird gestartet ...")
        worker = thread_worker(_bridge.start_server)(self.server)
        worker.returned.connect(lambda _: self._status("Server gestartet. Sobald die Brille ein Bild zeigt, "
                                                       "erscheint es hier."))
        worker.errored.connect(lambda error: self._status(f"{error}"))
        worker.finished.connect(lambda: setattr(self, "_presence_busy", False))
        worker.start()

    def _live_clicked(self, on: bool) -> None:
        self._auto_blocked = not on

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
            self._view_timer.stop()
            self._show_lead()
            self._status("Live-Ansicht aus.")
            return

        self.viewer.dims.ndisplay = 3
        _camera(self.viewer).perspective = LIVE_FOV_DEG
        self._live_worker = _poll_view(self.server)
        self._live_worker.yielded.connect(self._remember_view)
        self._live_worker.start()
        self._view_timer.start()
        # andersherum: drehen und zoomen in napari geht an die brille, solange die abgesetzt ist
        if not self._camera_connected:
            self._zoom_seen = float(_camera(self.viewer).zoom)
            _camera(self.viewer).events.angles.connect(self._camera_moved)
            _camera(self.viewer).events.zoom.connect(self._camera_zoomed)
            self._camera_connected = True
        self._show_lead()

    def _set_lead(self, index: int) -> None:
        self._lead = index
        for i, button in enumerate(self.lead_buttons):
            button.setChecked(i == index)
        # gleich die eigene ansicht schicken, sonst tut sich in der brille erst beim naechsten drehen was
        if index == LEAD_NAPARI and self._live_worker is not None:
            self._send_view(selected=-1)
        self._show_lead()

    def _show_lead(self) -> None:
        if self._live_worker is None:
            self.lead_label.setText("Kopplung ist aus")
        elif self._napari_leads():
            self.lead_label.setText("Jetzt steuert: napari (Maus und Tastatur)")
        else:
            self.lead_label.setText("Jetzt steuert: die Brille")

    def _napari_leads(self) -> bool:
        if self._lead == LEAD_VR:
            return False
        if self._lead == LEAD_NAPARI:
            return True
        return not self._vr_active or time.time() < self._napari_leads_until

    def _camera_moved(self, event=None) -> None:
        """mit maus oder tastatur gedreht, die brille dreht das volumen passend mit"""
        if self._live_worker is None or self._applying or self._lead == LEAD_VR:
            return
        # wer hier dreht fuehrt kurz, auch wenn die brille gerade getragen wird
        self._napari_leads_until = time.time() + 1.5
        if time.time() - self._last_sent < 0.06:
            self._send_timer.start()
            return
        self._send_view(selected=-1)

    def _send_trailing(self) -> None:
        if self._live_worker is not None and self._lead != LEAD_VR:
            self._send_view(selected=-1)

    def _camera_zoomed(self, event=None) -> None:
        """mit dem mausrad gezoomt, die brille skaliert das volumen um denselben faktor"""
        zoom = float(_camera(self.viewer).zoom)
        ratio = zoom / self._zoom_seen if self._zoom_seen else 1.0
        self._zoom_seen = zoom
        # zoom den die brille oder ein neu geladenes bild gesetzt hat zaehlt nicht mit
        if self._live_worker is None or self._applying or self._lead == LEAD_VR:
            return
        self._zoom_factor *= ratio
        self._camera_moved()

    def _send_view(self, selected: int) -> None:
        reference = self._reference_layer()
        if reference is None:
            return
        camera = _camera(self.viewer)
        view = _bridge.camera_to_view(camera.view_direction, camera.up_direction,
                                      _layer_shape(reference), reference.scale[-3:])
        # -1 heisst auswahl in der brille nicht anfassen
        view["selected"] = int(selected)
        view["zoom"] = self._zoom_factor
        view["zoomId"] = self._zoom_id
        self._last_sent = time.time()
        # nicht im fenster thread schicken, haengende anfrage friert sonst das drehen ein
        self._outbox = view
        self._outbox_ready.set()

    def _send_loop(self) -> None:
        """schickt immer nur die neueste ansicht, aeltere fallen weg"""
        while True:
            self._outbox_ready.wait()
            self._outbox_ready.clear()
            view, self._outbox = self._outbox, None
            if view is None:
                continue
            try:
                _bridge.post_napari_view(self.server, view)
            except Exception:
                pass

    def _label_picked(self, event=None) -> None:
        """in napari eine andere zelle gewaehlt, in der brille dieselbe auswaehlen"""
        layer = self._watched_labels
        if layer is None or self._applying or self._live_worker is None or self._lead == LEAD_VR:
            return
        self._last_selected = int(layer.selected_label)
        self._send_view(selected=int(layer.selected_label))

    def _follow_volume(self, name: str) -> None:
        """die brille zeigt ein bild das napari noch nicht hat, vom pc nachladen"""
        if not name or name == self._followed or self._fetching:
            return
        if name in self.viewer.layers:
            self._select_followed(name)
            return
        self._fetching = name
        self._status(f"Hole '{name}', das die Brille gerade zeigt ...")
        worker = thread_worker(_bridge.fetch_current)(self.server, name)
        worker.returned.connect(lambda data: self._show_current(name, data))
        worker.errored.connect(lambda error: self._follow_failed(name, error))
        worker.start()

    def _follow_failed(self, name: str, error) -> None:
        # als erledigt merken, sonst neuer versuch bei jedem blick
        self._fetching = None
        self._followed = name
        self._status(f"'{name}' nicht geholt: {error}")

    def _show_current(self, name: str, data: dict) -> None:
        self._fetching = None
        scale = tuple(float(v) for v in data["scale"])
        image = data["image"]
        labels = data.get("labels")
        # nur jede nte stelle je achse, blick rechnet ueber groesse mal scale und passt weiter
        step = _display_step(image.shape[1:]) if self.fast_check.isChecked() else 1
        if step > 1:
            image = image[:, ::step, ::step, ::step]
            labels = labels[::step, ::step, ::step] if labels is not None else None
            scale = tuple(s * step for s in scale)
        channel_names = [str(n) for n in data.get("channel_names", [])]
        colormaps = ["gray"] if image.shape[0] == 1 else ["red", "green", "blue"]
        # das erste bild stellt den zoom in napari neu ein, das soll nicht als zoomen an die brille
        self._applying = True
        try:
            for c in range(image.shape[0]):
                layer_name = name if c == 0 else f"{name} {channel_names[c] if c < len(channel_names) else c}"
                self.viewer.add_image(image[c], name=layer_name, scale=scale,
                                      colormap=colormaps[c % len(colormaps)],
                                      blending="translucent" if c == 0 else "additive", rendering="attenuated_mip")
        finally:
            self._applying = False
        if labels is not None:
            labels_layer = self.viewer.add_labels(labels, name=f"{name} labels", scale=scale)
            if "label_ids" in data:
                # name der struktur erscheint in der statuszeile wenn die maus drauf zeigt
                labels_layer.features = pd.DataFrame({
                    "index": [int(i) for i in data["label_ids"]],
                    "name": [str(n) for n in data["label_names"]],
                })
        self._select_followed(name)
        self._highlighted = None
        self._applied_view = None
        note = f", fuer die Anzeige jede {step}. Stelle" if step > 1 else ""
        self._status(f"'{name}' aus der Brille uebernommen{note}.")

    def _select_followed(self, name: str) -> None:
        """dieses bild ist ab jetzt die bezugsgroesse fuer den blick"""
        self._followed = name
        self._refresh_layers()
        for i in range(self.image_list.count()):
            item = self.image_list.item(i)
            item.setSelected(item.text() == name)
        index = self.labels_combo.findText(f"{name} labels")
        if index > 0:
            self.labels_combo.setCurrentIndex(index)

    def _remember_view(self, view: dict) -> None:
        self._latest_view = view

    def _apply_latest_view(self) -> None:
        view, self._latest_view = self._latest_view, None
        if view is not None:
            self._apply_view(view)

    @staticmethod
    def _pose(view: dict) -> tuple:
        return tuple(round(float(view[key][axis]), 3) for key in ("camPos", "camForward", "camUp") for axis in "xyz")

    def _apply_view(self, view: dict) -> None:
        self._status(_bridge.describe_view(view))
        self._vr_active = _bridge.vr_is_active(view)
        self._show_lead()
        if view.get("name"):
            self._follow_volume(str(view["name"]))
        reference = self._reference_layer()
        # fuehrt napari, nicht mit dem blick der brille ueberschreiben
        if self._napari_leads() or "camPos" not in view or reference is None:
            return

        # steht die brille still, nicht jedes mal neu zeichnen
        pose = (reference.name, self._pose(view))
        self._apply_selection(int(view.get("selected") or 0))
        if pose == self._applied_view:
            return
        self._applied_view = pose

        shape = _layer_shape(reference)
        state = _bridge.view_to_camera(view, shape, reference.scale[-3:], reference.translate[-3:],
                                       _canvas_size(self.viewer), LIVE_FOV_DEG, object_centric=True)
        camera = _camera(self.viewer)
        self._applying = True
        try:
            camera.center = state.center
            camera.set_view_direction(state.view_direction, up_direction=state.up_direction)
            if state.zoom is not None:
                camera.zoom = state.zoom
        finally:
            self._applying = False

    def _apply_selection(self, selected: int) -> None:
        labels_layer = self._selected_labels()
        if labels_layer is None:
            return
        if labels_layer is not self._watched_labels:
            labels_layer.events.selected_label.connect(self._label_picked)
            self._watched_labels = labels_layer
        if selected and selected != self._last_selected:
            self._applying = True
            try:
                labels_layer.selected_label = selected
            finally:
                self._applying = False
        # nur bei aenderung setzen, napari faerbt sonst jedes mal die ganze maske neu
        highlight = bool(selected) and self.highlight_check.isChecked()
        if highlight != self._highlighted:
            labels_layer.show_selected_label = highlight
            self._highlighted = highlight
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
        self._presence_timer.stop()
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
    """blick etwa zwanzigmal pro sekunde, fehler nur melden statt abzubrechen"""
    while True:
        try:
            yield _bridge.fetch_view(server)
            time.sleep(0.05)
        except Exception:
            yield {}
            time.sleep(2)


def _display_step(shape_zyx) -> int:
    voxels = float(np.prod(shape_zyx))
    return max(int(np.ceil((voxels / DISPLAY_VOXELS) ** (1.0 / 3.0))), 1)


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
