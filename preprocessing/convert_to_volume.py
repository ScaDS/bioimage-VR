"""2d bilderstapel zu 3d nifti volumen fuer den vr viewer.

phase 1, egal was inhaltlich drin ist, nur struktur, kein annotieren kein ki

eingabe geht als einzeldatei, remote url, oder ordner mit nummerierten slices

usage: python convert_to_volume.py input output.nii.gz
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import nibabel as nib
import numpy as np

SLICE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"}

# marker im nifti descrip feld, r g b sind rohe kanaele, farben im shader manuell synchron halten
LIVE_CHANNELS_MARKER = b"biovr:live_channels"

# perzentil statt min und max, einzelne hotpixel quetschen sonst das signal zusammen
LOW_PERCENTILE = 1.0
HIGH_PERCENTILE = 99.0


def _percentile_range(data: np.ndarray, low: float = LOW_PERCENTILE, high: float = HIGH_PERCENTILE) -> tuple[float, float]:
    lo, hi = np.percentile(data, [low, high])
    return float(lo), float(hi)


# 3x3x3 median vor dem strecken, nimmt den schleier um strukturen
DENOISE_MEDIAN_SIZE = 3


def _denoise(data: np.ndarray, size: int = DENOISE_MEDIAN_SIZE) -> np.ndarray:
    from scipy.ndimage import median_filter

    return median_filter(data, size=size)


# voxel budget gegen ruckeln auf der quest, 64 millionen voxel sind bei rgb ca 192 mb
MAX_VOXELS = 64_000_000


def _downsample_to_budget(volume: np.ndarray, max_voxels: int = MAX_VOXELS, channel_axis: int | None = None) -> np.ndarray:
    # isotrop verkleinern wenn die raeumlichen achsen das budget sprengen, kanal achse bleibt
    spatial_shape = tuple(n for axis, n in enumerate(volume.shape) if axis != channel_axis)
    spatial_voxels = int(np.prod(spatial_shape))
    if spatial_voxels <= max_voxels:
        return volume

    from scipy.ndimage import zoom

    factor = (max_voxels / spatial_voxels) ** (1.0 / 3.0)
    zoom_factors = tuple(1.0 if axis == channel_axis else factor for axis in range(volume.ndim))
    resized = zoom(volume, zoom_factors, order=1)
    new_spatial = tuple(n for axis, n in enumerate(resized.shape) if axis != channel_axis)
    print(f"downsample: {spatial_shape} -> {new_spatial} (faktor {factor:.3f}, "
          f"{spatial_voxels / 1e6:.0f}M -> {int(np.prod(new_spatial)) / 1e6:.0f}M voxel)")
    return resized


def is_remote(input_path: str) -> bool:
    # url erkennen, geht direkt an bioio ohne pfad checks
    return "://" in input_path


def is_zarr_folder(path: Path) -> bool:
    # zarr v2 hat .zgroup oder .zattrs im ordner, v3 eine zarr.json
    return path.suffix.lower() == ".zarr" or any((path / marker).exists() for marker in (".zgroup", ".zattrs", "zarr.json"))


def load_folder(path: Path, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    # ordner mit slices sortiert zu einem array, farbige slices bleiben rgb
    import imageio.v3 as iio
    from natsort import natsorted

    files = natsorted(
        (f for f in path.iterdir() if f.suffix.lower() in SLICE_EXTENSIONS),
        key=lambda f: f.name,
    )
    if not files:
        raise ValueError(f"No slice images found in {path}")

    slices = [_drop_alpha(np.asarray(iio.imread(f))) for f in files]

    shapes = {s.shape for s in slices}
    if len(shapes) > 1:
        raise ValueError(f"Slices have inconsistent shapes: {shapes}")

    stacked = np.stack(slices, axis=0)
    channel_axis = 3 if stacked.ndim == 4 else None
    return _downsample_to_budget(stacked, max_voxels, channel_axis=channel_axis)


def load_file(path_or_url: str, denoise: bool = True, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    # einzeldatei oder url laden, bioio erkennt das format
    from bioio import BioImage

    img = BioImage(path_or_url)
    return _composite_channels(_extract_czyx(img.data, max_voxels), denoise=denoise)


def _extract_czyx(data: np.ndarray, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    # 5d tczyx auf zeitpunkt 0, danach gleich aufs budget verkleinern
    if data.ndim != 5:
        raise ValueError(f"Expected a 5D TCZYX array from bioio, got shape {data.shape}")
    volume = data[0]  # erster zeitpunkt, alle kanaele
    if volume.ndim != 4:
        raise ValueError(f"Unexpected volume shape after reduction: {volume.shape}")
    return _downsample_to_budget(volume, max_voxels, channel_axis=0)


def _composite_channels(volume_czyx: np.ndarray, denoise: bool = True) -> np.ndarray:
    # ein kanal bleibt grau, mehrere roh je r g b fuer live faerbung, hoechstens drei
    channel_count = volume_czyx.shape[0]
    if channel_count == 1:
        # unveraendert zurueck, entrauscht wird in convert
        return volume_czyx[0]

    used = min(channel_count, 3)
    if channel_count > used:
        print(f"warnung: {channel_count} kanaele gefunden, nur die ersten {used} werden genutzt")

    stacked = np.zeros(volume_czyx.shape[1:] + (3,), dtype=np.uint8)
    for i in range(used):
        channel = volume_czyx[i].astype(np.float32)
        if denoise:
            channel = _denoise(channel)
        lo, hi = _percentile_range(channel)
        normalized = np.clip((channel - lo) / (hi - lo), 0.0, 1.0) if hi > lo else np.zeros_like(channel)
        stacked[..., i] = np.clip(normalized * 255.0, 0, 255).astype(np.uint8)

    return stacked


def _drop_alpha(slice_: np.ndarray) -> np.ndarray:
    # alphakanal verwerfen falls vorhanden, graustufen/rgb sonst unveraendert lassen
    if slice_.ndim == 2:
        return slice_
    if slice_.ndim == 3 and slice_.shape[-1] in (3, 4):
        return slice_[..., :3]
    raise ValueError(f"Unexpected slice shape: {slice_.shape}")


def normalize_to_uint8(volume: np.ndarray, denoise: bool = True) -> np.ndarray:
    # per perzentil auf uint8 strecken, vorher entrauschen
    volume = volume.astype(np.float32)
    if denoise:
        volume = _denoise(volume)
    lo, hi = _percentile_range(volume)
    if hi <= lo:
        return np.zeros_like(volume, dtype=np.uint8)
    scaled = (volume - lo) / (hi - lo) * 255.0
    return np.clip(scaled, 0, 255).astype(np.uint8)


def _to_nifti_image(volume: np.ndarray, live_channels: bool = False) -> nib.Nifti1Image:
    # (z,y,x) nach (x,y,z) drehen, nibabel nimmt achse 0 als x, rgb als nifti rgb24
    if volume.ndim == 4:
        volume = volume.transpose(2, 1, 0, 3)  # (z,y,x,3) -> (x,y,z,3)
        packed = np.zeros(volume.shape[:3], dtype=[("R", "u1"), ("G", "u1"), ("B", "u1")])
        packed["R"], packed["G"], packed["B"] = volume[..., 0], volume[..., 1], volume[..., 2]
        data = packed
    else:
        data = volume.transpose(2, 1, 0)  # (z,y,x) -> (x,y,z)
    # identitaets affine, kein klinischer scan
    image = nib.Nifti1Image(data, affine=np.eye(4))
    if live_channels:
        image.header["descrip"] = LIVE_CHANNELS_MARKER
    return image


def convert(input_path: str, output_path: Path, denoise: bool = True, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    if is_remote(input_path):
        volume = load_file(input_path, denoise=denoise, max_voxels=max_voxels)
        live_channels = volume.ndim == 4
    else:
        local_path = Path(input_path)
        # ein lokales zarr ist auch ein ordner, aber kein stapel aus einzelbildern
        if local_path.is_dir() and not is_zarr_folder(local_path):
            volume = load_folder(local_path, max_voxels=max_voxels)
            live_channels = False  # bereits fertige farbbilder, keine rohen kanaele zum mischen
        else:
            volume = load_file(str(local_path), denoise=denoise, max_voxels=max_voxels)
            live_channels = volume.ndim == 4

    # (z,y,x,3) ist schon normalisiert, nur graustufen brauchen noch das strecken
    if volume.ndim == 3:
        volume = normalize_to_uint8(volume, denoise=denoise)

    image = _to_nifti_image(volume, live_channels=live_channels)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    nib.save(image, str(output_path))
    return volume


def convert_array(volume_czyx: np.ndarray, output_path: Path, denoise: bool = False,
                  max_voxels: int = MAX_VOXELS) -> np.ndarray:
    # schon geladenes (c,z,y,x) statt datei, kette wie convert, entrauschen aus
    if volume_czyx.ndim != 4:
        raise ValueError(f"Expected a (c,z,y,x) array, got shape {volume_czyx.shape}")
    volume = _downsample_to_budget(volume_czyx, max_voxels, channel_axis=0)
    volume = _composite_channels(volume, denoise=denoise)
    live_channels = volume.ndim == 4
    if volume.ndim == 3:
        volume = normalize_to_uint8(volume, denoise=denoise)

    image = _to_nifti_image(volume, live_channels=live_channels)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    nib.save(image, str(output_path))
    return volume


# einheiten die die app kennt, umrechnung nach um fuer das alte feld voxel_size_um
UNIT_TO_UM = {"nm": 0.001, "um": 1.0, "µm": 1.0, "micron": 1.0, "mm": 1000.0, "cm": 10000.0}


def read_tiff_spacing(path: str) -> tuple[list[float], str, tuple[int, ...]] | None:
    # voxelgroesse (z,y,x), einheit und form aus imagej tiff, sonst None
    if is_remote(path) or Path(path).suffix.lower() not in (".tif", ".tiff"):
        return None
    import tifffile

    with tifffile.TiffFile(path) as tif:
        meta = tif.imagej_metadata or {}
        page = tif.pages[0]
        series = tif.series[0]
        # ueber die achsennamen, bei mehrkanal stapeln (ZCYX) ist shape[-3] sonst der kanal
        dims = dict(zip(series.axes, series.shape))
        shape = (dims.get("Z", 1), dims.get("Y", series.shape[-2]), dims.get("X", series.shape[-1]))
        unit = str(meta.get("unit", "")).replace("\\u00B5", "µ").strip()
        if not unit or unit == "pixel":
            return None

        def per_unit(tag: str) -> float | None:
            if tag not in page.tags:
                return None
            num, den = page.tags[tag].value
            return den / num if num else None

        x, y = per_unit("XResolution"), per_unit("YResolution")
        z = float(meta.get("spacing", x or 1.0))
        if x is None or y is None:
            return None
    return [z, y, x], unit, shape


def write_metadata(output_path: Path, voxel_zyx: list[float], unit: str, shape_zyx: tuple[int, ...],
                   channels: list[str] | None = None) -> Path:
    # <stem>_metadata.json neben das volumen, voxel_size_um fuer aeltere app staende
    stem = output_path.name.removesuffix(".gz").removesuffix(".nii")
    to_um = UNIT_TO_UM.get(unit, 1.0)
    unit = "µm" if unit in ("um", "micron") else unit
    metadata = {
        "name": stem,
        "source": "file",
        "size": {"x": shape_zyx[2], "y": shape_zyx[1], "z": shape_zyx[0]},
        "unit": unit,
        "voxel_size": {"x": voxel_zyx[2], "y": voxel_zyx[1], "z": voxel_zyx[0]},
        "voxel_size_um": {"x": voxel_zyx[2] * to_um, "y": voxel_zyx[1] * to_um, "z": voxel_zyx[0] * to_um},
        "channels": channels or [],
    }
    path = output_path.with_name(f"{stem}_metadata.json")
    path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False), encoding="utf-8")
    return path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_path", help="2D slice folder, a single stack file, or a remote URL (e.g. OME-Zarr)")
    parser.add_argument("output_path", type=Path, help="Output .nii.gz path")
    parser.add_argument("--no-denoise", dest="denoise", action="store_false",
                         help="Median-Filter vor dem Kontrast-Strecken deaktivieren (zum Vergleichen)")
    parser.add_argument("--max-voxels", type=int, default=MAX_VOXELS,
                         help=f"Downsample-Schwelle in raeumlichen Voxeln, Standard {MAX_VOXELS:,} (siehe MAX_VOXELS)")
    parser.add_argument("--voxel-size", type=float, nargs=3, metavar=("X", "Y", "Z"),
                         help="Voxelgroesse, falls die Datei keine mitbringt (ImageJ-TIFFs werden automatisch gelesen)")
    parser.add_argument("--unit", default="um", help="Einheit zu --voxel-size, z.B. um oder mm")
    parser.set_defaults(denoise=True)
    args = parser.parse_args()

    volume = convert(args.input_path, args.output_path, denoise=args.denoise, max_voxels=args.max_voxels)
    print(f"Wrote {args.output_path} - shape={volume.shape} dtype={volume.dtype}")

    written_zyx = tuple(volume.shape[:3])
    spacing = None
    if args.voxel_size:
        x, y, z = args.voxel_size
        spacing = ([z, y, x], args.unit, written_zyx)
    else:
        spacing = read_tiff_spacing(args.input_path)
    if spacing:
        voxel_zyx, unit, original_zyx = spacing
        # verkleinert heisst groessere voxel
        voxel_zyx = [v * o / w for v, o, w in zip(voxel_zyx, original_zyx, written_zyx)]
        path = write_metadata(args.output_path, voxel_zyx, unit, written_zyx)
        print(f"Wrote {path} - voxel {voxel_zyx} {unit} (z,y,x)")


if __name__ == "__main__":
    main()
