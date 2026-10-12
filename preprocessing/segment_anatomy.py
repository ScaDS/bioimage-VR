"""organe und knochen in einem koerperbild automatisch benennen (TotalSegmentator).

ausgabe neben dem volumen, dieselbe schiene wie die zell segmentierung:
    <stem>_labels.nii.gz          maske, VolumeView laedt sie automatisch mit
    <stem>_labels_names.json      name je id, die brille zeigt dann den namen statt "Cell N"

laeuft nur bei koerperbildern: einheit mm und mindestens ein paar zentimeter gross.
mikroskopie (µm) wird uebersprungen, dort hat ein organ modell nichts zu suchen

zwei stufen: erst das grundmodell fuer den ganzen koerper. findet es ein gehirn, ist ein
kopf im bild, dann mit --detail zusaetzlich die kopf modelle (nase, rachen, gaumen, zunge,
kaumuskeln). die sind mit ct trainiert, auf mrt treffen sie die region, raender sind grob

das modell braucht die echte voxelgroesse und die lage im koerper. voxelgroesse kommt aus
dem metadata.json. die lage steht bei dicom und nifti vom geraet in der datei, bei tiff
nicht, dann mit --orientation angeben: drei buchstaben, wohin die achsen x y z der nii
datei zeigen, aus R L (rechts links), A P (vorn hinten), S I (oben unten).
einmal angegeben wird sie im metadata.json gemerkt

TotalSegmentator liegt in einer eigenen umgebung (torch ist gross), pfad ueber
TOTALSEG_ENV, sonst C:/Users/PC/totalseg-env

usage:
    python segment_anatomy.py data/haase_mrt/haase_mrt_head.nii.gz --orientation PIR
    python segment_anatomy.py volume.nii.gz --modality ct --fast --push
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import nibabel as nib
import numpy as np

from convert_labels import labels_path_for
from deploy_to_headset import DEFAULT_PACKAGE, check_single_device, find_adb, push_to_device

DEFAULT_ENV = Path(r"C:\Users\PC\totalseg-env")
# darunter ist es kein koerperbild mehr, mm
MIN_BODY_EXTENT = 50.0
TASKS = {"mr": "total_mr", "ct": "total"}
# zusatzmodelle je koerperregion, headneck_bones_vessels fand am mrt kopf nichts
DETAIL_TASKS = {"brain": ["head_glands_cavities", "head_muscles"]}
DIRECTIONS = {"R": (1, 0, 0), "L": (-1, 0, 0), "A": (0, 1, 0), "P": (0, -1, 0), "S": (0, 0, 1), "I": (0, 0, -1)}


def names_path_for(volume_path: Path) -> Path:
    return labels_path_for(volume_path).with_name(labels_path_for(volume_path).name.replace(".nii.gz", "_names.json"))


def metadata_path_for(volume_path: Path) -> Path | None:
    stem = volume_path.name.removesuffix(".gz").removesuffix(".nii")
    for candidate in (volume_path.with_name(f"{stem}_metadata.json"), volume_path.with_name("metadata.json")):
        if candidate.exists():
            return candidate
    return None


def affine_for(orientation: str, voxel: np.ndarray) -> np.ndarray:
    # affine aus achsenrichtungen und voxelgroesse, ursprung egal
    letters = orientation.upper()
    if len(letters) != 3 or any(c not in DIRECTIONS for c in letters):
        raise ValueError(f"Orientation '{orientation}' must be three letters out of R L A P S I, e.g. PIR.")
    pairs = {frozenset("RL"), frozenset("AP"), frozenset("SI")}
    if {next(p for p in pairs if c in p) for c in letters} != pairs:
        raise ValueError(f"Orientation '{orientation}' names a body axis twice.")
    affine = np.eye(4)
    for axis, letter in enumerate(letters):
        affine[:3, axis] = np.array(DIRECTIONS[letter]) * voxel[axis]
    return affine


def is_body_scan(shape: tuple[int, ...], voxel: np.ndarray, unit: str) -> bool:
    return unit == "mm" and float(np.max(np.array(shape[:3]) * voxel)) >= MIN_BODY_EXTENT


def class_names(env: Path, task: str) -> dict[int, str]:
    # namen der klassen stehen nur in der totalseg umgebung
    code = ("import json; from totalsegmentator.map_to_binary import class_map; "
            f"print(json.dumps(class_map['{task}']))")
    out = subprocess.run([str(env / "Scripts" / "python.exe"), "-c", code], capture_output=True, text=True, check=True)
    return {int(k): v.replace("_", " ") for k, v in json.loads(out.stdout.strip().splitlines()[-1]).items()}


def run_task(source: Path, task: str, fast: bool, env: Path) -> np.ndarray:
    exe = env / "Scripts" / "TotalSegmentator.exe"
    if not exe.exists():
        raise FileNotFoundError(f"TotalSegmentator not found at {exe}. Set TOTALSEG_ENV or install it there.")
    target = source.with_name(f"labels_{task}.nii.gz")
    command = [str(exe), "-i", str(source), "-o", str(target), "--task", task, "--ml", "--device", "cpu"]
    if fast:
        command.append("--fast")
    subprocess.run(command, check=True)
    return np.asanyarray(nib.load(str(target)).dataobj).astype(np.uint16)


def read_voxel_size(metadata: dict) -> tuple[np.ndarray | None, str]:
    size = metadata.get("voxel_size")
    unit = metadata.get("unit", "")
    # alte und idr metadaten kennen nur voxel_size_um, das ist mikroskopie
    if not size and metadata.get("voxel_size_um"):
        size, unit = metadata["voxel_size_um"], "um"
    if not size:
        return None, unit
    return np.array([size["x"], size["y"], size["z"]], dtype=float), unit


def segment_body(volume_path: Path, orientation: str | None = None, modality: str = "mr", fast: bool = False,
                 detail: bool = False, overwrite: bool = False) -> dict:
    # grundmodell und detailmodelle, schreibt maske und namen, ValueError mit lesbarem grund
    labels_path = labels_path_for(volume_path)
    if labels_path.exists() and not overwrite:
        raise ValueError(f"{labels_path} already exists. It may be a hand made segmentation, "
                         "pass --overwrite to replace it.")

    metadata_path = metadata_path_for(volume_path)
    metadata = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path else {}
    voxel, _ = read_voxel_size(metadata)
    if voxel is None:
        raise ValueError("No voxel_size in the metadata.json next to the volume, the model needs the real size.")
    orientation = orientation or metadata.get("orientation")
    if not orientation:
        raise ValueError("The file does not say how the body lies in it. Pass --orientation, e.g. PIR "
                         "(x points to the back, y down, z to the right).")

    env = Path(os.environ.get("TOTALSEG_ENV", DEFAULT_ENV))
    data = np.asanyarray(nib.load(str(volume_path)).dataobj)
    if data.ndim == 4:
        data = data.max(axis=3)

    # ids ueber alle modelle fortlaufend neu vergeben, jedes modell zaehlt sonst wieder ab 1
    merged = np.zeros(data.shape, dtype=np.uint16)
    entries: list[dict] = []
    sources: list[str] = []

    def add(task: str, labels: np.ndarray) -> list[str]:
        if labels.shape != data.shape:
            raise ValueError(f"Model {task} returned shape {labels.shape}, volume is {data.shape}.")
        names = class_names(env, task)
        found = []
        for i in (int(v) for v in np.unique(labels) if v):
            # was ein frueheres modell schon benannt hat bleibt stehen
            region = (labels == i) & (merged == 0)
            count = int(region.sum())
            if count == 0:
                continue
            new_id = len(entries) + 1
            merged[region] = new_id
            name = names.get(i, f"structure {i}")
            entries.append({"id": new_id, "name": name, "voxels": count, "model": task})
            found.append(name)
        return found

    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "input.nii.gz"
        image = nib.Nifti1Image(np.ascontiguousarray(data), affine_for(orientation, voxel))
        image.header.set_xyzt_units("mm")
        nib.save(image, str(source))

        base = TASKS[modality]
        found = add(base, run_task(source, base, fast, env))
        sources.append(f"{base}{' fast' if fast else ''}")
        if not found:
            raise ValueError("The model found nothing. Check --orientation and --modality.")
        if detail:
            for trigger, tasks in DETAIL_TASKS.items():
                if trigger not in found:
                    continue
                for task in tasks:
                    print(f"'{trigger}' found, running {task}")
                    add(task, run_task(source, task, False, env))
                    sources.append(task)

    image = nib.Nifti1Image(np.ascontiguousarray(merged), affine=np.eye(4))
    image.set_data_dtype(np.uint16)
    nib.save(image, str(labels_path))
    names_path = names_path_for(volume_path)
    names_path.write_text(json.dumps({
        "kind": "anatomy",
        "source": "TotalSegmentator " + ", ".join(sources),
        "labels": [{"id": e["id"], "name": e["name"]} for e in entries],
    }, indent=1), encoding="utf-8")

    # lage merken, naechster lauf und spaeter die brille brauchen sie nicht nochmal
    if metadata_path and metadata.get("orientation") != orientation.upper():
        metadata["orientation"] = orientation.upper()
        metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    voxel_ml = float(np.prod(voxel)) / 1000.0
    return {
        "labels_path": labels_path,
        "names_path": names_path,
        "models": sources,
        "structures": [{"name": e["name"], "ml": round(e["voxels"] * voxel_ml, 1), "model": e["model"]} for e in entries],
    }


def push(paths: list[Path], package: str | None = None, adb: str | None = None) -> None:
    adb = adb or find_adb()
    check_single_device(adb)
    for path in paths:
        push_to_device(adb, path, package or DEFAULT_PACKAGE)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("volume_path", type=Path, help="The converted .nii.gz")
    parser.add_argument("--orientation", help="Where x y z of the file point in the body, e.g. PIR")
    parser.add_argument("--modality", choices=sorted(TASKS), default="mr")
    parser.add_argument("--fast", action="store_true", help="3 mm base model, seconds instead of a minute on a CPU")
    parser.add_argument("--detail", action="store_true", help="Also run the region models, e.g. nose and throat for a head")
    parser.add_argument("--force", action="store_true", help="Run even if this does not look like a body scan")
    parser.add_argument("--overwrite", action="store_true", help="Replace an existing _labels file")
    parser.add_argument("--push", action="store_true", help="Also adb push mask and names to the headset")
    parser.add_argument("--package", help="Android application id (default: see deploy_to_headset.py)")
    parser.add_argument("--adb", help="Path to adb.exe (default: auto-detect)")
    args = parser.parse_args()

    if not args.volume_path.is_file():
        sys.exit(f"Volume '{args.volume_path}' not found.")

    metadata_path = metadata_path_for(args.volume_path)
    metadata = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path else {}
    voxel, unit = read_voxel_size(metadata)
    shape = nib.load(str(args.volume_path)).shape
    if voxel is not None and not is_body_scan(shape, voxel, unit) and not args.force:
        extent = float(np.max(np.array(shape[:3]) * voxel))
        sys.exit(f"Skipped: {extent:.3g} {unit or 'units'} across does not look like a body scan "
                 f"(needs mm and at least {MIN_BODY_EXTENT:.0f} mm). Use a cell model for microscopy, or --force.")

    try:
        result = segment_body(args.volume_path, args.orientation, args.modality, args.fast, args.detail, args.overwrite)
    except (ValueError, FileNotFoundError) as e:
        sys.exit(str(e))

    print(f"Wrote {result['labels_path']}")
    print(f"Wrote {result['names_path']}")
    for item in sorted(result["structures"], key=lambda t: -t["ml"]):
        print(f"  {item['name']:28s} {item['ml']:9.1f} ml   {item['model']}")
    if args.push:
        push([result["labels_path"], result["names_path"]], args.package, args.adb)


if __name__ == "__main__":
    main()
