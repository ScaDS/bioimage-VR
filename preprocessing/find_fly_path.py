"""flugweg durch einen hohlraum im volumen, z.b. nase bis luftroehre.

alles unter dem threshold ist luft, pro luftvoxel der abstand zur naechsten wand
der weg ist der guenstigste von start nach ziel, nah an der wand ist teuer, dadurch
bleibt er mittig im gang. allgemein fuer jedes bild mit hohlraeumen, nichts koerperspezifisches

start und ziel in voxeln (x,y,z) wie in der nifti datei, sie rasten auf die
freieste luftstelle in der naehe ein, grob geschaetzt reicht also

stationen kommen aus der form des wegs, ohne anatomie wissen: eingang aus der freien luft,
engstellen und scharfe kurven. namen dafuer kann spaeter ein modell oder das vlm vergeben

ausgabe <volumen stem>_flypath.json neben dem volumen, dazu ein kontrollbild

usage:
    python find_fly_path.py volume.nii.gz --start 8,225,119 --end 150,312,124
    python find_fly_path.py volume.nii.gz --start 8,225,119 --end 150,312,124 --threshold 0.12
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import nibabel as nib
import numpy as np
from scipy import ndimage

PATH_SUFFIX = "_flypath"
# gleicher startwert wie der threshold regler in der brille
DEFAULT_THRESHOLD = 0.12
# glaettung vor dem threshold in voxeln, sonst loechert rauschen die waende
SMOOTH_SIGMA = 0.6
# so weit darf start und ziel zur naechsten freien stelle springen
SNAP_RADIUS = 8
# abstand der gespeicherten wegpunkte
SPACING_VOXELS = 2.0
# endet der weg so nah am bildrand, laeuft er gerade weiter aus dem bild hinaus
BORDER_VOXELS = 12.0
# so weit geht er dann ueber den rand hinaus, in der brille sieht man von dort wieder alles
OUTSIDE_VOXELS = 30.0


def load_volume(path: Path) -> tuple[np.ndarray, np.ndarray, str]:
    """graustufen 0..1 als (x,y,z), voxelgroesse und einheit aus dem metadata.json daneben"""
    data = np.asanyarray(nib.load(str(path)).dataobj)
    if data.ndim == 4:
        data = data.max(axis=3)
    volume = data.astype(np.float32) / 255.0

    stem = path.name.removesuffix(".gz").removesuffix(".nii")
    voxel = np.ones(3, dtype=np.float32)
    unit = "px"
    for candidate in (path.with_name(f"{stem}_metadata.json"), path.with_name("metadata.json")):
        if candidate.exists():
            meta = json.loads(candidate.read_text(encoding="utf-8"))
            size = meta.get("voxel_size") or meta.get("voxel_size_um")
            if size:
                voxel = np.array([size["x"], size["y"], size["z"]], dtype=np.float32)
                unit = meta.get("unit", "um") if meta.get("voxel_size") else "um"
            break
    return volume, voxel, unit


def clearance_map(volume: np.ndarray, threshold: float, voxel: np.ndarray) -> np.ndarray:
    """abstand zur naechsten wand in der einheit des volumens, 0 im gewebe"""
    smooth = ndimage.gaussian_filter(volume, SMOOTH_SIGMA)
    air = smooth < threshold
    return ndimage.distance_transform_edt(air, sampling=voxel).astype(np.float32)


def snap(point: np.ndarray, clearance: np.ndarray, radius: int = SNAP_RADIUS) -> tuple[int, int, int]:
    """freieste luftstelle im umkreis"""
    lo = np.maximum(point - radius, 0)
    hi = np.minimum(point + radius + 1, clearance.shape)
    box = clearance[lo[0]:hi[0], lo[1]:hi[1], lo[2]:hi[2]]
    if box.max() <= 0:
        raise ValueError(f"No air within {radius} voxels of {tuple(int(p) for p in point)}. "
                         "Move the point or raise --threshold.")
    best = np.unravel_index(int(box.argmax()), box.shape)
    return tuple(int(lo[i] + best[i]) for i in range(3))


def find_path(clearance: np.ndarray, start: tuple[int, ...], end: tuple[int, ...],
              voxel: np.ndarray, cap: float) -> np.ndarray:
    """guenstigster weg nur durch luft, kosten fallen mit dem wandabstand"""
    from skimage.graph import MCP_Geometric

    # ab cap bringt mehr abstand nichts mehr, sonst zieht freie aussenluft den weg aus dem gang
    capped = np.minimum(clearance, cap)
    costs = np.where(clearance > 0, (cap / np.maximum(capped, 1e-3)) ** 2, -1.0)
    mcp = MCP_Geometric(costs, fully_connected=True, sampling=tuple(float(v) for v in voxel))
    mcp.find_costs([start], [end])
    return np.array(mcp.traceback(end), dtype=np.float32)


def smooth_and_resample(path: np.ndarray, spacing: float = SPACING_VOXELS) -> np.ndarray:
    """treppenstufen vom voxelraster weg, danach gleichmaessige abstaende"""
    if len(path) < 5:
        return path
    smooth = ndimage.gaussian_filter1d(path, sigma=3.0, axis=0, mode="nearest")
    smooth[0], smooth[-1] = path[0], path[-1]
    seg = np.linalg.norm(np.diff(smooth, axis=0), axis=1)
    along = np.concatenate([[0.0], np.cumsum(seg)])
    samples = np.linspace(0.0, along[-1], max(int(along[-1] / spacing) + 1, 2))
    return np.stack([np.interp(samples, along, smooth[:, i]) for i in range(3)], axis=1)


def _distance_to_border(point: np.ndarray, direction: np.ndarray, shape: tuple[int, ...]) -> float:
    """strecke in voxeln bis der strahl das bild verlaesst"""
    best = np.inf
    for axis in range(3):
        if abs(direction[axis]) < 1e-6:
            continue
        edge = (shape[axis] - 1) if direction[axis] > 0 else 0.0
        best = min(best, (edge - point[axis]) / direction[axis])
    return float(best)


def extend_outside(points: np.ndarray, shape: tuple[int, ...], spacing: float = SPACING_VOXELS) -> np.ndarray:
    """ein ende nah am bildrand gerade weiterfuehren bis ausserhalb des bilds.
    ein angeschnittener gang, z.b. die luftroehre am unteren bildrand, endet sonst mitten im koerper"""
    def tail(end: np.ndarray, before: np.ndarray) -> np.ndarray:
        direction = end - before
        length = np.linalg.norm(direction)
        if length < 1e-6:
            return np.empty((0, 3))
        direction = direction / length
        to_border = _distance_to_border(end, direction, shape)
        if not 0 <= to_border <= BORDER_VOXELS:
            return np.empty((0, 3))
        steps = np.arange(spacing, to_border + OUTSIDE_VOXELS + spacing, spacing)
        return end + steps[:, None] * direction

    back = min(5, len(points) - 1)
    head = tail(points[0], points[back])[::-1]
    foot = tail(points[-1], points[-1 - back])
    return np.concatenate([head, points, foot])


def inside_volume(points: np.ndarray, shape: tuple[int, ...]) -> np.ndarray:
    return np.all((points >= 0) & (points <= np.array(shape) - 1), axis=1)


def sample_clearance(clearance: np.ndarray, points: np.ndarray, open_value: float) -> np.ndarray:
    """ausserhalb des bilds gibt es keine wand, dort gilt der gang als offen"""
    radius = ndimage.map_coordinates(clearance, points.T, order=1, mode="nearest")
    radius[~inside_volume(points, clearance.shape)] = open_value
    return radius


def find_stations(points: np.ndarray, radius: np.ndarray, length: np.ndarray, voxel: np.ndarray,
                  cap: float, unit: str) -> list[dict]:
    """auffaellige stellen entlang des wegs, rein aus der geometrie"""
    from scipy.signal import find_peaks

    stations: list[dict] = []
    inside = 0
    outside = len(radius)
    below = np.nonzero(radius < 0.8 * cap)[0]
    # startet der weg in freier luft, ist der eingang die stelle wo es eng wird
    if radius[0] >= cap and len(below):
        inside = int(below[0])
        stations.append({"index": inside, "kind": "entrance", "label": "Entrance"})
    # endet er in freier luft oder ausserhalb des bilds, ist der ausgang die letzte enge stelle
    if radius[-1] >= cap and len(below):
        outside = int(below[-1]) + 1
        stations.append({"index": outside - 1, "kind": "exit", "label": "Exit"})

    spacing = float(np.median(np.diff(length)))
    gap = max(int(15.0 * float(voxel.min()) / spacing), 1)

    # engstellen, deutlich schmaler als davor und danach
    inner = radius[inside:outside]
    dips, props = find_peaks(-inner, prominence=max(0.25 * float(np.median(inner)), 0.5 * float(voxel.min())),
                             distance=gap)
    order = np.argsort(props["prominences"])[::-1][:4]
    for i in sorted(int(dips[k]) + inside for k in order):
        stations.append({"index": i, "kind": "narrowing",
                         "label": f"Narrowing {radius[i] * 2:.1f} {unit}"})

    # kurven, richtungswechsel ueber ein stueck weg davor und danach
    world = points * voxel
    angle = np.zeros(len(points))
    for i in range(max(gap, inside), min(len(points) - gap, outside)):
        a = world[i] - world[i - gap]
        b = world[i + gap] - world[i]
        cos = float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))
        angle[i] = np.degrees(np.arccos(np.clip(cos, -1.0, 1.0)))
    bends, _ = find_peaks(angle[inside:outside], height=50.0, distance=2 * gap)
    for i in (int(b) + inside for b in bends):
        stations.append({"index": i, "kind": "bend", "label": f"Bend {angle[i]:.0f} deg"})

    stations.sort(key=lambda s: s["index"])
    for s in stations:
        s["distance"] = round(float(length[s["index"]]), 1)
    return stations


def write_preview(volume: np.ndarray, points: np.ndarray, radius: np.ndarray, length: np.ndarray,
                  unit: str, out: Path, stations: list[dict]) -> None:
    """weg ueber drei schnitte gelegt, dazu die gangbreite entlang des wegs"""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    mid = np.round(np.median(points, axis=0)).astype(int)
    views = [
        (volume[:, :, mid[2]].T, 0, 1, f"z = {mid[2]}"),
        (volume[mid[0], :, :].T, 1, 2, f"x = {mid[0]}"),
        (volume[:, mid[1], :].T, 0, 2, f"y = {mid[1]}"),
    ]
    fig, axes = plt.subplots(1, 4, figsize=(24, 6.5))
    for ax, (image, a, b, title) in zip(axes, views):
        ax.imshow(image, cmap="gray", origin="lower", vmax=0.6)
        ax.scatter(points[:, a], points[:, b], c=radius, cmap="autumn", s=6)
        ax.plot(points[0, a], points[0, b], "o", color="lime", ms=9)
        ax.plot(points[-1, a], points[-1, b], "s", color="cyan", ms=9)
        ax.set_title(f"{title}, path projected  ({'xyz'[a]} horizontal, {'xyz'[b]} vertical)")
    axes[3].plot(length, radius * 2)
    for n, s in enumerate(stations, 1):
        i = s["index"]
        axes[0].annotate(str(n), (points[i, 0], points[i, 1]), color="white", fontsize=13, weight="bold",
                         xytext=(6, 6), textcoords="offset points")
        axes[3].axvline(length[i], color="gray", lw=0.8)
        axes[3].text(length[i], axes[3].get_ylim()[1] * (0.95 - 0.07 * (n % 4)), f" {n} {s['label']}", fontsize=9)
    axes[3].set_xlabel(f"distance along path ({unit})")
    axes[3].set_ylabel(f"free width ({unit})")
    axes[3].set_title("free width along the path")
    axes[3].grid(alpha=0.3)
    fig.tight_layout()
    fig.savefig(out, dpi=70)
    plt.close(fig)


def parse_point(text: str) -> np.ndarray:
    parts = [int(p) for p in text.split(",")]
    if len(parts) != 3:
        raise argparse.ArgumentTypeError("expected x,y,z")
    return np.array(parts)


def main() -> None:
    parser = argparse.ArgumentParser(description="Centered fly path through a cavity of a volume.")
    parser.add_argument("volume", type=Path)
    parser.add_argument("--start", type=parse_point, required=True, help="voxel x,y,z")
    parser.add_argument("--end", type=parse_point, required=True, help="voxel x,y,z")
    parser.add_argument("--threshold", type=float, default=DEFAULT_THRESHOLD)
    parser.add_argument("--cap", type=float, default=None,
                        help="wall distance beyond which more room does not matter, default 6 voxels")
    args = parser.parse_args()

    volume, voxel, unit = load_volume(args.volume)
    clearance = clearance_map(volume, args.threshold, voxel)
    start = snap(args.start, clearance)
    end = snap(args.end, clearance)
    cap = args.cap if args.cap is not None else 6.0 * float(voxel.min())

    raw = find_path(clearance, start, end, voxel, cap)
    if len(raw) < 2:
        sys.exit("No connected air path between start and end. Lower or raise --threshold, or move the points.")
    points = extend_outside(smooth_and_resample(raw), clearance.shape)
    radius = sample_clearance(clearance, points, cap)
    step = np.linalg.norm(np.diff(points * voxel, axis=0), axis=1)
    length = np.concatenate([[0.0], np.cumsum(step)])

    stations = find_stations(points, radius, length, voxel, cap, unit)

    stem = args.volume.name.removesuffix(".gz").removesuffix(".nii")
    out_json = args.volume.with_name(f"{stem}{PATH_SUFFIX}.json")
    out_json.write_text(json.dumps({
        "volume": args.volume.name,
        "threshold": args.threshold,
        "unit": unit,
        "length": round(float(length[-1]), 2),
        "min_radius": round(float(radius.min()), 3),
        "stations": stations,
        "points": [{"x": round(float(p[0]), 2), "y": round(float(p[1]), 2), "z": round(float(p[2]), 2),
                    "radius": round(float(r), 3)} for p, r in zip(points, radius)],
    }, indent=1), encoding="utf-8")
    out_png = args.volume.with_name(f"{stem}{PATH_SUFFIX}.png")
    write_preview(volume, points, radius, length, unit, out_png, stations)

    narrow = int(radius.argmin())
    print(f"start {start}  end {end}")
    print(f"length {length[-1]:.1f} {unit}, {len(points)} points")
    print(f"narrowest {radius.min() * 2:.2f} {unit} wide at {length[narrow]:.1f} {unit} "
          f"(voxel {tuple(int(v) for v in points[narrow])}), median width {np.median(radius) * 2:.2f} {unit}")
    for n, s in enumerate(stations, 1):
        print(f"  station {n}: {s['label']} at {s['distance']} {unit}, voxel "
              f"{tuple(int(v) for v in points[s['index']])}")
    print(f"wrote {out_json}")
    print(f"wrote {out_png}")


if __name__ == "__main__":
    main()
