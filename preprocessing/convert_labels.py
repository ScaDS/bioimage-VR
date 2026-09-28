"""fertige segmentierung (label maske) zu nifti neben das passende volumen.

maske ist ein label bild, 0 hintergrund, 1..n je eine zelle (z.b. cellpose, nessys)
ausgabe <volumen stem>_labels.nii.gz im selben ordner, VolumeView laedt die automatisch mit

kein kontrast strecken, kein entrauschen, labels bleiben exakt
achsen layout kommt vom zielvolumen, alte dateien vor dem transpose fix (22.09.)
haben x und z noch vertauscht, beides wird erkannt

usage:
    python convert_labels.py masks.tiff data/idr/6001240/idr_6001240_original.nii.gz
    python convert_labels.py masks.tiff volume.nii.gz --push
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import nibabel as nib
import numpy as np

LABELS_SUFFIX = "_labels"


def labels_path_for(volume_path: Path) -> Path:
    stem = volume_path.name.removesuffix(".gz").removesuffix(".nii")
    return volume_path.with_name(f"{stem}{LABELS_SUFFIX}.nii.gz")


def load_mask(path: Path) -> np.ndarray:
    """maske als (z,y,x), einzelne 2d maske wird zu einer schicht"""
    import tifffile

    mask = np.squeeze(tifffile.imread(str(path)))
    if mask.ndim == 2:
        mask = mask[None]
    if mask.ndim != 3:
        raise ValueError(f"Expected a 2D or 3D label mask, got shape {mask.shape}")
    if not np.issubdtype(mask.dtype, np.integer):
        raise ValueError(f"Label mask must be integer typed, got {mask.dtype}")
    if mask.min() < 0 or mask.max() > 65535:
        raise ValueError(f"Label ids must fit into uint16, got range {mask.min()}..{mask.max()}")
    return mask


def match_volume_layout(mask_zyx: np.ndarray, volume_shape: tuple[int, ...]) -> np.ndarray:
    """maske in dasselbe (nifti) achsen layout wie das volumen bringen"""
    spatial = tuple(volume_shape[:3])
    xyz = mask_zyx.transpose(2, 1, 0)
    if xyz.shape == spatial:
        return xyz
    # altes layout, volumen wurde ohne transpose gespeichert
    if mask_zyx.shape == spatial:
        print("hinweis: volumen hat noch das alte achsen layout, maske wird genauso abgelegt")
        return mask_zyx
    raise ValueError(
        f"Mask shape {mask_zyx.shape} (z,y,x) does not match volume shape {spatial}. "
        "Was the volume downsampled during conversion (--max-voxels)? The mask has to be "
        "made for exactly this volume."
    )


def convert_labels(mask_path: Path, volume_path: Path) -> Path:
    mask = load_mask(mask_path)
    volume = nib.load(str(volume_path))
    labels = match_volume_layout(mask, volume.shape).astype(np.uint16)

    image = nib.Nifti1Image(np.ascontiguousarray(labels), affine=np.eye(4))
    image.set_data_dtype(np.uint16)
    output_path = labels_path_for(volume_path)
    nib.save(image, str(output_path))

    count = len(np.unique(labels)) - 1
    print(f"Wrote {output_path} - shape={labels.shape} labels={count} max_id={labels.max()}")
    return output_path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mask_path", type=Path, help="Label mask (.tif/.tiff), 0 = background")
    parser.add_argument("volume_path", type=Path, help="The already converted .nii.gz the mask belongs to")
    parser.add_argument("--push", action="store_true", help="Also adb push the labels file to the headset")
    parser.add_argument("--package", help="Android application id (default: see deploy_to_headset.py)")
    parser.add_argument("--adb", help="Path to adb.exe (default: auto-detect)")
    args = parser.parse_args()

    if not args.volume_path.is_file():
        sys.exit(f"Volume '{args.volume_path}' not found.")

    output_path = convert_labels(args.mask_path, args.volume_path)
    if not args.push:
        return

    from deploy_to_headset import DEFAULT_PACKAGE, check_single_device, find_adb, push_to_device

    adb = args.adb or find_adb()
    check_single_device(adb)
    push_to_device(adb, output_path, args.package or DEFAULT_PACKAGE)
    print("Das zugehoerige Volumen muss auf der Brille denselben Dateinamen ohne '_labels' haben.")


if __name__ == "__main__":
    main()
