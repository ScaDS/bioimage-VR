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

# marker im nifti "descrip" header-feld (siehe _to_nifti_image) - sagt der unity-seite
# dass r,g,b drei ROHE, unabhaengige kanal-intensitaeten sind statt einer fertig
# gemischten farbe. faerbung/kanal an-aus passiert dann live im shader
# (VolumeRaymarch.shader _IsLiveChannels), reihenfolge/standardfarben pro kanal-index
# sind dort per _ChannelColor0/1/2 hinterlegt (kanal 0 = rot, 1 = gruen, 2 = blau) -
# muss manuell synchron gehalten werden, gibt's noch keine gemeinsame quelle fuer
LIVE_CHANNELS_MARKER = b"biovr:live_channels"

# perzentil statt echtem min/max fuers strecken - mikroskopiedaten haben fast immer
# einzelne extrem helle ausreisser (sensor hotpixel, saettigung), die bei echtem max
# das eigentliche signal in einen winzigen unteren wertebereich zusammenquetschen
LOW_PERCENTILE = 1.0
HIGH_PERCENTILE = 99.0


def _percentile_range(data: np.ndarray, low: float = LOW_PERCENTILE, high: float = HIGH_PERCENTILE) -> tuple[float, float]:
    lo, hi = np.percentile(data, [low, high])
    return float(lo), float(hi)


# kantenerhaltender 3x3x3 median filter vor dem kontrast-strecken - reduziert genau das
# restrauschen (einzelne helle/dunkle voxel), das nach dem strecken als duenner
# schleier/nebel um strukturen sichtbar wird. per --no-denoise abschaltbar zum vergleichen
DENOISE_MEDIAN_SIZE = 3


def _denoise(data: np.ndarray, size: int = DENOISE_MEDIAN_SIZE) -> np.ndarray:
    from scipy.ndimage import median_filter

    return median_filter(data, size=size)


# groesse-limit gegen quest-gpu-ruckler: eine 3763x2860x145 (~1.56 mrd. voxel) textur hat
# beim laden sichtbar geruckelt (siehe STATUS.md 2026-07-28 diagnose). 64 millionen voxel
# sind bei rgb24 (3 byte/voxel) ~192mb, bei graustufen (1 byte/voxel) ~64mb texturgroesse -
# beides gut tragbar, waehrend normal grosse bilder (ueblich 15-20m voxel) unangetastet
# bleiben. greift vor dem entrauschen/kontrast-strecken, spart bei riesigen bildern auch
# unnoetige rechenzeit dort
MAX_VOXELS = 64_000_000


def _downsample_to_budget(volume: np.ndarray, max_voxels: int = MAX_VOXELS, channel_axis: int | None = None) -> np.ndarray:
    """volumen isotrop verkleinern falls die raeumlichen achsen (alle ausser channel_axis)
    zusammen das voxel-budget sprengen wuerden. faktor wirkt gleich auf alle raeumlichen
    achsen (isotrop, proportionen bleiben erhalten) - ein evtl. channel_axis (fuer bereits
    kompositierte rgb-bilder bzw. rohe kanaele) bleibt unangetastet (zoom-faktor 1 dort)"""
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
    """url erkennen, geht direkt an bioio ohne pfad checks"""
    return "://" in input_path


def load_folder(path: Path, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    """ordner mit slices laden, sortiert, zu einem array. bereits farbige slices
    (z.b. gerenderte screenshots) bleiben rgb statt auf grau reduziert zu werden"""
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
    """einzeldatei oder url laden, bioio erkennt format selbst.
    mehrere kanaele werden zu einem rgb volumen kompositiert (siehe _composite_channels)"""
    from bioio import BioImage

    img = BioImage(path_or_url)
    return _composite_channels(_extract_czyx(img.data, max_voxels), denoise=denoise)


def _extract_czyx(data: np.ndarray, max_voxels: int = MAX_VOXELS) -> np.ndarray:
    """5d tczyx array auf zeitpunkt 0 reduzieren, kanaele bleiben erhalten. downsamplet
    danach falls das voxel-budget ueberschritten waere (siehe _downsample_to_budget) -
    hier statt spaeter, damit entrauschen/kontrast-strecken nicht auf der vollen groesse
    laufen muss"""
    if data.ndim != 5:
        raise ValueError(f"Expected a 5D TCZYX array from bioio, got shape {data.shape}")
    volume = data[0]  # erster zeitpunkt, alle kanaele
    if volume.ndim != 4:
        raise ValueError(f"Unexpected volume shape after reduction: {volume.shape}")
    return _downsample_to_budget(volume, max_voxels, channel_axis=0)


def _composite_channels(volume_czyx: np.ndarray, denoise: bool = True) -> np.ndarray:
    """einzelkanal bleibt graustufen (z,y,x), unveraendert (aber ggf. entrauscht).
    mehrere kanaele werden je einzeln entrauscht und 0..255 normalisiert, OHNE farbe
    zu verrechnen - landen roh (ein kanal pro r/g/b) im ergebnis, damit die unity-app
    sie live faerben/mischen und einzeln an-ausschalten kann (siehe LIVE_CHANNELS_MARKER).
    kappt bei mehr als 3 kanaelen mit warnung auf die ersten drei"""
    channel_count = volume_czyx.shape[0]
    if channel_count == 1:
        # unveraendert zurueck, normalize_to_uint8 in convert() entrauscht/streckt diesen
        # fall gleich noch (ndim==3 pfad) - hier nicht doppelt entrauschen
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
    """alphakanal verwerfen falls vorhanden, graustufen/rgb sonst unveraendert lassen"""
    if slice_.ndim == 2:
        return slice_
    if slice_.ndim == 3 and slice_.shape[-1] in (3, 4):
        return slice_[..., :3]
    raise ValueError(f"Unexpected slice shape: {slice_.shape}")


def normalize_to_uint8(volume: np.ndarray, denoise: bool = True) -> np.ndarray:
    """werte per perzentil-kontrast auf uint8 skalieren, siehe _percentile_range.
    entrauscht vorher per default (siehe _denoise)"""
    volume = volume.astype(np.float32)
    if denoise:
        volume = _denoise(volume)
    lo, hi = _percentile_range(volume)
    if hi <= lo:
        return np.zeros_like(volume, dtype=np.uint8)
    scaled = (volume - lo) / (hi - lo) * 255.0
    return np.clip(scaled, 0, 255).astype(np.uint8)


def _to_nifti_image(volume: np.ndarray, live_channels: bool = False) -> nib.Nifti1Image:
    """graustufen (z,y,x) direkt, rgb (z,y,x,3) als nifti rgb24 (datatype 128) -
    standardkonform, jedes 'voxel' 3 interleaved bytes r,g,b statt eine extra dimension.
    live_channels setzt LIVE_CHANNELS_MARKER im descrip-header-feld (80 byte freitext,
    fuer genau sowas gedacht) - unterscheidet "r,g,b sind drei rohe kanaele" von
    "r,g,b ist schon eine fertige farbe" (z.b. bereits farbige slice-bilder)"""
    if volume.ndim == 4:
        packed = np.zeros(volume.shape[:3], dtype=[("R", "u1"), ("G", "u1"), ("B", "u1")])
        packed["R"], packed["G"], packed["B"] = volume[..., 0], volume[..., 1], volume[..., 2]
        data = packed
    else:
        data = volume
    # identitaets affine, kein klinischer scan, keine umorientierung
    # spaeter nochmal pruefen falls phase 2 koordinaten mapping was braucht
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
        if local_path.is_dir():
            volume = load_folder(local_path, max_voxels=max_voxels)
            live_channels = False  # bereits fertige farbbilder, keine rohen kanaele zum mischen
        else:
            volume = load_file(str(local_path), denoise=denoise, max_voxels=max_voxels)
            live_channels = volume.ndim == 4

    # (z,y,x,3) ist beim laden schon entrauscht/normalisiert (siehe _composite_channels
    # bzw. bereits farbige slices), nur graustufen braucht noch den kontrast-stretch
    if volume.ndim == 3:
        volume = normalize_to_uint8(volume, denoise=denoise)

    image = _to_nifti_image(volume, live_channels=live_channels)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    nib.save(image, str(output_path))
    return volume


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_path", help="2D slice folder, a single stack file, or a remote URL (e.g. OME-Zarr)")
    parser.add_argument("output_path", type=Path, help="Output .nii.gz path")
    parser.add_argument("--no-denoise", dest="denoise", action="store_false",
                         help="Median-Filter vor dem Kontrast-Strecken deaktivieren (zum Vergleichen)")
    parser.add_argument("--max-voxels", type=int, default=MAX_VOXELS,
                         help=f"Downsample-Schwelle in raeumlichen Voxeln, Standard {MAX_VOXELS:,} (siehe MAX_VOXELS)")
    parser.set_defaults(denoise=True)
    args = parser.parse_args()

    volume = convert(args.input_path, args.output_path, denoise=args.denoise, max_voxels=args.max_voxels)
    print(f"Wrote {args.output_path} - shape={volume.shape} dtype={volume.dtype}")


if __name__ == "__main__":
    main()
