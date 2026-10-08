"""tests fuer _bridge ohne napari, laufen mit pytest oder direkt mit python"""

import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from napari_bioimage_vr import _bridge  # noqa: E402


def test_to_czyx_shapes():
    assert _bridge.to_czyx(np.zeros((5, 6, 7))).shape == (1, 5, 6, 7)
    assert _bridge.to_czyx(np.zeros((6, 7))).shape == (1, 1, 6, 7)
    assert _bridge.to_czyx(np.zeros((2, 5, 6, 7))).shape == (2, 5, 6, 7)
    assert _bridge.to_czyx(np.zeros((5, 6, 7, 3)), rgb=True).shape == (3, 5, 6, 7)
    assert _bridge.to_czyx(np.zeros((6, 7, 4)), rgb=True).shape == (3, 1, 6, 7)


def test_bundle_rejects_wrong_mask():
    try:
        _bridge.build_bundle([np.zeros((5, 6, 7))], np.zeros((5, 6, 8), dtype=np.int32), (1, 1, 1), ["a"])
    except ValueError:
        return
    raise AssertionError("falsche maskengroesse muss abgelehnt werden")


def _view(pos, forward, up):
    xyz = lambda v: {"x": v[0], "y": v[1], "z": v[2]}
    return {"camPos": xyz(pos), "camForward": xyz(forward), "camUp": xyz(up)}


def test_camera_looks_at_volume_center():
    # kamera vor dem volumen auf der z achse, schaut hinein, y nach oben
    shape, scale, translate = (100, 200, 300), (2.0, 1.0, 1.0), (0.0, 0.0, 0.0)
    state = _bridge.view_to_camera(_view((0, 0, -1), (0, 0, 1), (0, 1, 0)), shape, scale, translate, 800, 60)

    center_expected = (np.array(shape) - 1) / 2 * np.array(scale)
    assert np.allclose(state.center, center_expected, atol=1e-6)
    # unity z ist napari achse 0, unity y ist achse 1
    assert np.allclose(state.view_direction, (1, 0, 0))
    assert np.allclose(state.up_direction, (0, 1, 0))
    # abstand bis zur mitte = eine volumen tiefe, 200 welteinheiten
    distance = 200.0
    assert math.isclose(state.zoom, 800 / (2 * distance * math.tan(math.radians(30))), rel_tol=1e-6)


def test_camera_direction_respects_anisotropic_voxels():
    # diagonal im wuerfel, physikalisch aber flacher weil z nur halb so gross ist
    shape, scale = (10, 10, 10), (0.5, 1.0, 1.0)
    state = _bridge.view_to_camera(_view((-1, 0, -1), (1, 0, 1), (0, 1, 0)), shape, scale, (0, 0, 0), 800, 60)
    direction = np.array(state.view_direction)
    assert direction[2] > direction[0] > 0


if __name__ == "__main__":
    for name, test in list(globals().items()):
        if name.startswith("test_"):
            test()
            print("ok", name)
