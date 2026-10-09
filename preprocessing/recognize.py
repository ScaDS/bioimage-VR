"""erkennt stufenweise was ein bild zeigt und stoesst das passende modell an.

stufe 1  bildart: koerperbild (mrt, ct) oder mikroskopie
         erst regeln aus den metadaten (einheit und echte groesse), dann schaut das vlm auf
         drei schnitte und nennt modalitaet und inhalt. bei widerspruch gewinnt die regel
stufe 2  koerperbild: grundmodell fuer organe und knochen, siehe segment_anatomy.py
         mikroskopie: zellen und kerne mit Cellpose-SAM, rechnet auf dem cluster
stufe 3  koerperbild: detailmodelle je gefundener region, z.b. nase und rachen beim kopf

ergebnis: maske und namen neben dem volumen wie bei jeder segmentierung, dazu ein block
"recognition" im metadata.json mit bildart, modalitaet, inhalt und den gelaufenen stufen

usage:
    python recognize.py data/haase_mrt/haase_mrt_head.nii.gz --orientation PIR
    python recognize.py volume.nii.gz --no-vlm --fast
    python recognize.py volume.nii.gz --push
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import sys
import urllib.request
from pathlib import Path

import nibabel as nib
import numpy as np

import hpc
from convert_labels import labels_path_for, write_labels_array
from segment_anatomy import MIN_BODY_EXTENT, metadata_path_for, push, read_voxel_size, segment_body

VLM_BASE = "https://llm.scads.ai/v1"
VLM_MODEL = "alias-vision"
API_KEY_FILE = Path.home() / ".scadsai-api-key"
# mikroskopie ist kleiner als das, in mm
MAX_MICROSCOPY_EXTENT = 5.0
UNIT_TO_MM = {"nm": 1e-6, "um": 1e-3, "µm": 1e-3, "micron": 1e-3, "mm": 1.0, "cm": 10.0, "m": 1000.0}
MODALITY_TO_MODEL = {"mri": "mr", "ct": "ct"}

VLM_SYSTEM = (
    "You classify 3D scientific images. You get three orthogonal middle slices and some facts "
    "from the file. Reply with JSON only, no other text:\n"
    '{"kind": "body_scan" | "microscopy" | "other", '
    '"modality": "mri" | "ct" | "fluorescence" | "brightfield" | "electron" | "other", '
    '"subject": "<what is shown, a few words>", "confidence": "low" | "medium" | "high"}\n'
    "body_scan means a medical scan of a body or body part, microscopy means cells or tissue "
    "under a microscope."
)


def extent_mm(shape: tuple[int, ...], voxel: np.ndarray | None, unit: str) -> float | None:
    if voxel is None or unit not in UNIT_TO_MM:
        return None
    return float(np.max(np.array(shape[:3]) * voxel)) * UNIT_TO_MM[unit]


def kind_from_metadata(shape: tuple[int, ...], voxel: np.ndarray | None, unit: str) -> tuple[str | None, str]:
    """bildart allein aus der echten groesse, None wenn die nichts hergibt"""
    size = extent_mm(shape, voxel, unit)
    if size is None:
        return None, "no physical size in the metadata"
    if size >= MIN_BODY_EXTENT:
        return "body_scan", f"{size:.0f} mm across"
    if size <= MAX_MICROSCOPY_EXTENT:
        return "microscopy", f"{size * 1000:.0f} µm across"
    return None, f"{size:.1f} mm across fits neither a body scan nor microscopy"


def middle_slices_png(volume_path: Path) -> bytes:
    """drei mittlere schnitte nebeneinander, so sieht das vlm jede bildart gleich"""
    from PIL import Image

    data = np.asanyarray(nib.load(str(volume_path)).dataobj)
    # farbvolumen liegen als rgb24 in der nifti, numpy liest das als feld mit R G B
    if data.dtype.names:
        data = np.stack([data[name] for name in data.dtype.names], axis=-1)
    color = data.ndim == 4
    cx, cy, cz = (n // 2 for n in data.shape[:3])
    slices = [data[:, :, cz], data[cx, :, :], data[:, cy, :]]
    tiles = []
    for s in slices:
        tile = np.swapaxes(s, 0, 1)[::-1]
        if not color:
            tile = np.repeat(tile[..., None], 3, axis=2)
        # mrt und mikroskopie nutzen selten den ganzen wertebereich
        tile = np.clip(tile.astype(np.float32) * 1.6, 0, 255).astype(np.uint8)
        image = Image.fromarray(tile[..., :3])
        scale = 384 / max(image.size)
        tiles.append(image.resize((max(int(image.width * scale), 1), max(int(image.height * scale), 1))))
    sheet = Image.new("RGB", (sum(t.width for t in tiles) + 16, max(t.height for t in tiles)), (0, 0, 0))
    x = 0
    for t in tiles:
        sheet.paste(t, (x, 0))
        x += t.width + 8
    buffer = io.BytesIO()
    sheet.save(buffer, format="PNG")
    return buffer.getvalue()


def facts_for_vlm(metadata: dict, shape: tuple[int, ...], voxel: np.ndarray | None, unit: str) -> str:
    lines = [f"voxels: {shape[0]} x {shape[1]} x {shape[2]}"]
    size = extent_mm(shape, voxel, unit)
    if size is not None:
        lines.append(f"physical size: {size:.3g} mm across")
    if metadata.get("channels"):
        lines.append("channels: " + ", ".join(str(c) for c in metadata["channels"]))
    for key, value in (metadata.get("key_values") or {}).items():
        lines.append(f"{key}: {value}")
    return "\n".join(lines)


def ask_vlm(png: bytes, facts: str) -> dict:
    payload = {
        "model": VLM_MODEL,
        "temperature": 0,
        "messages": [
            {"role": "system", "content": VLM_SYSTEM},
            {"role": "user", "content": [
                {"type": "text", "text": "Facts from the file:\n" + facts},
                {"type": "image_url", "image_url": {"url": "data:image/png;base64," + base64.b64encode(png).decode()}},
            ]},
        ],
    }
    request = urllib.request.Request(
        f"{VLM_BASE}/chat/completions", data=json.dumps(payload).encode(),
        headers={"Authorization": f"Bearer {API_KEY_FILE.read_text().strip()}", "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=120) as response:
        text = json.loads(response.read())["choices"][0]["message"]["content"]
    # modelle packen json gern in einen codeblock
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        raise ValueError(f"no JSON in the answer: {text[:120]}")
    return json.loads(text[start:end + 1])


def classify(volume_path: Path, metadata: dict, use_vlm: bool) -> dict:
    """stufe 1, regel und vlm zusammenfuehren"""
    shape = nib.load(str(volume_path)).shape
    voxel, unit = read_voxel_size(metadata)
    rule_kind, rule_reason = kind_from_metadata(shape, voxel, unit)

    vlm: dict = {}
    vlm_error = None
    if use_vlm:
        try:
            vlm = ask_vlm(middle_slices_png(volume_path), facts_for_vlm(metadata, shape, voxel, unit))
        except Exception as e:  # netz weg oder kein key, die regel traegt dann allein
            vlm_error = f"{type(e).__name__}: {e}"

    kind = rule_kind or vlm.get("kind") or "unknown"
    notes = []
    if rule_kind and vlm.get("kind") and vlm["kind"] != rule_kind:
        notes.append(f"vlm said {vlm['kind']}, the physical size says {rule_kind}, size wins")
    if vlm_error:
        notes.append(f"vlm not available ({vlm_error})")
    return {
        "kind": kind,
        "modality": vlm.get("modality", "unknown"),
        "subject": vlm.get("subject", ""),
        "decided_by": "size" if rule_kind else "vlm" if vlm.get("kind") else "nothing",
        "size_rule": rule_reason,
        "vlm_confidence": vlm.get("confidence", ""),
        "notes": notes,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("volume_path", type=Path, help="The converted .nii.gz")
    parser.add_argument("--orientation", help="Body scans only: where x y z of the file point, e.g. PIR")
    parser.add_argument("--no-vlm", action="store_true", help="Decide from the metadata alone")
    parser.add_argument("--no-detail", action="store_true", help="Body scans: skip the region models, base model only")
    parser.add_argument("--fast", action="store_true", help="Body scans: 3 mm base model")
    parser.add_argument("--overwrite", action="store_true", help="Replace an existing _labels file")
    parser.add_argument("--classify-only", action="store_true", help="Stop after level 1")
    parser.add_argument("--push", action="store_true", help="Also adb push the results to the headset")
    args = parser.parse_args()

    if not args.volume_path.is_file():
        sys.exit(f"Volume '{args.volume_path}' not found.")
    metadata_path = metadata_path_for(args.volume_path)
    metadata = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path else {}

    recognition = classify(args.volume_path, metadata, use_vlm=not args.no_vlm)
    print(f"level 1  kind: {recognition['kind']}  ({recognition['decided_by']}: {recognition['size_rule']})")
    if recognition["modality"] != "unknown" or recognition["subject"]:
        print(f"         vlm: {recognition['modality']}, {recognition['subject']} "
              f"(confidence {recognition['vlm_confidence'] or 'not given'})")
    for note in recognition["notes"]:
        print(f"         note: {note}")

    steps: list[str] = []
    to_push: list[Path] = []
    if args.classify_only:
        pass
    elif recognition["kind"] == "body_scan":
        modality = MODALITY_TO_MODEL.get(recognition["modality"])
        if modality is None:
            modality = "mr"
            print("level 2  modality not known, assuming MRI")
        try:
            result = segment_body(args.volume_path, args.orientation, modality, args.fast,
                                  detail=not args.no_detail, overwrite=args.overwrite)
        except (ValueError, FileNotFoundError) as e:
            print(f"level 2  anatomy not run: {e}")
            steps.append(f"anatomy skipped: {e}")
        else:
            steps += result["models"]
            to_push += [result["labels_path"], result["names_path"]]
            print(f"level 2 and 3  models: {', '.join(result['models'])}")
            for item in sorted(result["structures"], key=lambda t: -t["ml"]):
                print(f"         {item['name']:26s} {item['ml']:9.1f} ml   {item['model']}")
    elif recognition["kind"] == "microscopy":
        if labels_path_for(args.volume_path).exists() and not args.overwrite:
            print("level 2  a segmentation already lies next to the volume, nothing to do")
            steps.append("cells: existing segmentation kept")
        else:
            print("level 2  cells: Cellpose-SAM on the cluster")
            try:
                masks, summary = hpc.segment_cells(args.volume_path, hpc.cellpose_anisotropy(args.volume_path))
            except (RuntimeError, TimeoutError, OSError) as e:
                print(f"         not run: {e}")
                steps.append(f"cells skipped: {e}")
            else:
                to_push.append(write_labels_array(masks, args.volume_path))
                steps.append(f"cellpose-sam: {summary['cells']} cells")
                print(f"         {summary['cells']} cells in {summary['seconds']} s, job {summary['job_id']}")
    else:
        print("level 2  nothing to run for this kind of image")

    if metadata_path:
        recognition["steps"] = steps
        metadata["recognition"] = recognition
        metadata_path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False), encoding="utf-8")
        to_push.append(metadata_path)
        print(f"wrote recognition into {metadata_path.name}")

    if args.push and to_push:
        push(to_push)


if __name__ == "__main__":
    main()
