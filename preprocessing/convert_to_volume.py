"""2d bilderstapel zu 3d nifti volumen fuer den vr viewer.

phase 1, egal was inhaltlich drin ist, nur struktur, kein annotieren kein ki

eingabe geht als einzeldatei, remote url, oder ordner mit nummerierten slices

usage: python convert_to_volume.py input output.nii.gz
"""

from __future__ import annotations

import argparse
from pathlib import Path

import nibabel as nib
import numpy as np

SLICE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"}


def is_remote(input_path: str) -> bool:
    """url erkennen, geht direkt an bioio ohne pfad checks"""
    return "://" in input_path


def load_folder(path: Path) -> np.ndarray:
    """ordner mit slices laden, sortiert, zu einem array"""
    import imageio.v3 as iio
    from natsort import natsorted

    files = natsorted(
        (f for f in path.iterdir() if f.suffix.lower() in SLICE_EXTENSIONS),
        key=lambda f: f.name,
    )
    if not files:
        raise ValueError(f"No slice images found in {path}")

    slices = [_to_grayscale(np.asarray(iio.imread(f))) for f in files]

    shapes = {s.shape for s in slices}
    if len(shapes) > 1:
        raise ValueError(f"Slices have inconsistent shapes: {shapes}")

    return np.stack(slices, axis=0)


def load_file(path_or_url: str) -> np.ndarray:
    """einzeldatei oder url laden, bioio erkennt format selbst"""
    from bioio import BioImage

    img = BioImage(path_or_url)
    return _reduce_to_zyx(img.data)


def _reduce_to_zyx(data: np.ndarray) -> np.ndarray:
    """5d tczyx array auf einen kanal reduzieren, mehrkanal kommt spaeter"""
    if data.ndim != 5:
        raise ValueError(f"Expected a 5D TCZYX array from bioio, got shape {data.shape}")
    volume = data[0, 0]  # erster zeitpunkt erster kanal
    if volume.ndim != 3:
        raise ValueError(f"Unexpected volume shape after reduction: {volume.shape}")
    return volume


def _to_grayscale(slice_: np.ndarray) -> np.ndarray:
    """slice mit farbkanal zu grauwert reduzieren"""
    if slice_.ndim == 2:
        return slice_
    if slice_.ndim == 3:
        return slice_[..., 0]
    raise ValueError(f"Unexpected slice shape: {slice_.shape}")


def normalize_to_uint8(volume: np.ndarray) -> np.ndarray:
    """werte auf uint8 skalieren"""
    volume = volume.astype(np.float32)
    lo, hi = float(volume.min()), float(volume.max())
    if hi <= lo:
        return np.zeros_like(volume, dtype=np.uint8)
    scaled = (volume - lo) / (hi - lo) * 255.0
    return scaled.astype(np.uint8)


def convert(input_path: str, output_path: Path) -> np.ndarray:
    if is_remote(input_path):
        volume = load_file(input_path)
    else:
        local_path = Path(input_path)
        volume = load_folder(local_path) if local_path.is_dir() else load_file(str(local_path))
    volume = normalize_to_uint8(volume)

    # identitaets affine, kein klinischer scan, keine umorientierung
    # spaeter nochmal pruefen falls phase 2 koordinaten mapping was braucht
    image = nib.Nifti1Image(volume, affine=np.eye(4))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    nib.save(image, str(output_path))
    return volume


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_path", help="2D slice folder, a single stack file, or a remote URL (e.g. OME-Zarr)")
    parser.add_argument("output_path", type=Path, help="Output .nii.gz path")
    args = parser.parse_args()

    volume = convert(args.input_path, args.output_path)
    print(f"Wrote {args.output_path} - shape={volume.shape} dtype={volume.dtype}")


if __name__ == "__main__":
    main()
