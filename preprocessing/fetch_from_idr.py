"""idr bild per id lokal runterladen, als nifti volumen plus metadaten json.

sucht automatisch den passenden ome zarr pfad auf s3 (idr nutzt mehrere
versionen je nach studie), konvertiert dann wie convert_to_volume.py und
laedt zusaetzlich key value metadaten von der idr api (organismus,
protokoll, segmentierungsmethode etc) dazu, nur zum ablegen, das interface
liest das noch nicht.

standardmaessig nur lokal, mit --push zusaetzlich aufs verbundene quest
(nutzt dieselbe push logik wie deploy_to_headset.py)

geht auch fuer ein ganzes dataset (--dataset) oder eine ganze studie/projekt
mit mehreren datasets (--project)

usage:
    python fetch_from_idr.py 6001240
    python fetch_from_idr.py 6001240 --name mein_sample
    python fetch_from_idr.py 6001240 --push
    python fetch_from_idr.py --dataset 7754 --push
    python fetch_from_idr.py --project 801 --push
"""

from __future__ import annotations

import argparse
import json
import urllib.error
import urllib.request
from pathlib import Path

from convert_to_volume import MAX_VOXELS, convert
from deploy_to_headset import DEFAULT_PACKAGE, check_single_device, ensure_api_key_pushed, find_adb, push_to_device

DATA_ROOT = Path(__file__).resolve().parent.parent / "data" / "idr"
API_BASE = "https://idr.openmicroscopy.org"

# reihenfolge testen, erste antwortende url gewinnt, idr hat je nach
# studie unterschiedliche zarr versionen im bucket liegen
ZARR_URL_PATTERNS = [
    "https://uk1s3.embassy.ebi.ac.uk/idr/zarr/v0.4/{id}.zarr",
    "https://uk1s3.embassy.ebi.ac.uk/idr/zarr/v0.1/{id}.zarr",
]


def resolve_zarr_url(image_id: int) -> str:
    """passende zarr url fuer die id finden, testet die patterns der reihe nach"""
    for pattern in ZARR_URL_PATTERNS:
        url = pattern.format(id=image_id)
        request = urllib.request.Request(f"{url}/.zattrs", method="HEAD")
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                if response.status == 200:
                    return url
        except urllib.error.URLError:
            continue
    raise RuntimeError(
        f"Bild {image_id} hat kein OME-Zarr-Volumen bei IDR (getestete Pfade: "
        f"{[p.format(id=image_id) for p in ZARR_URL_PATTERNS]}). Kommt vor, z.B. bei "
        f"2D-Objekttraeger-Scans oder aelteren Datasets ohne Zarr-Konvertierung - "
        f"anderes Bild aus dem Browser probieren."
    )


def has_zarr(image_id: int) -> bool:
    """ob ein bild ueberhaupt ein ome-zarr volumen hat, ohne die url selbst zu brauchen -
    fuers vorab-markieren in der browse-galerie. viele idr-bilder, v.a. aeltere studien,
    wurden nie zu zarr konvertiert (siehe resolve_zarr_url) - ohne diese markierung
    klickt man sich sonst quasi zufaellig durch fehlschlaege (bei einer stichprobe von
    20 studien hatten nur 2 ueberhaupt zarr)"""
    try:
        resolve_zarr_url(image_id)
        return True
    except Exception:
        return False


def get_size_z(image_id: int) -> int | None:
    """nur SizeZ eines bildes holen, ohne key value annotationen zu laden - leichter
    als fetch_metadata(), fuers massenhafte pruefen in is_3d() beim durchblaettern"""
    with urllib.request.urlopen(f"{API_BASE}/api/v0/m/images/{image_id}/", timeout=15) as response:
        image = json.load(response)["data"]
    return image.get("Pixels", {}).get("SizeZ")


def is_3d(image_id: int) -> bool:
    """ob ein bild ueberhaupt ein 3d stack ist (SizeZ>1) - sehr viele idr quellen sind
    2d objekttraeger scans, fuer den vr volumen viewer ohnehin ungeeignet selbst wenn
    sie zufaellig ein zarr volumen haetten (stichprobe 25.08.: 18 von 30 studien hatten
    im ersten bild SizeZ>1, deutlich besser als die zarr trefferquote - trotzdem lohnt
    sich das rausfiltern, siehe idr_list level=images in upload_server.py)"""
    try:
        size_z = get_size_z(image_id)
        return bool(size_z and size_z > 1)
    except Exception:
        return False


def fetch_metadata(image_id: int) -> dict:
    """bildinfo und key value annotationen von der idr api holen"""
    with urllib.request.urlopen(f"{API_BASE}/api/v0/m/images/{image_id}/", timeout=15) as response:
        image = json.load(response)["data"]

    with urllib.request.urlopen(
        f"{API_BASE}/webclient/api/annotations/?image={image_id}&type=map", timeout=15
    ) as response:
        annotations = json.load(response)["annotations"]

    key_values = {}
    for annotation in annotations:
        for key, value in annotation.get("values", []):
            key_values[key] = value

    pixels = image.get("Pixels", {})

    def voxel_size(field: str) -> float | None:
        entry = pixels.get(field)
        return entry.get("Value") if entry else None

    return {
        "image_id": image_id,
        "name": image.get("Name"),
        "description": image.get("Description") or None,
        "size": {
            "x": pixels.get("SizeX"),
            "y": pixels.get("SizeY"),
            "z": pixels.get("SizeZ"),
            "c": pixels.get("SizeC"),
            "t": pixels.get("SizeT"),
        },
        "voxel_size_um": {
            "x": voxel_size("PhysicalSizeX"),
            "y": voxel_size("PhysicalSizeY"),
            "z": voxel_size("PhysicalSizeZ"),
        },
        "channels": [channel.get("Name") for channel in pixels.get("Channels", [])],
        "key_values": key_values,
    }


def list_dataset_image_ids(dataset_id: int) -> list[int]:
    """alle image ids in einem dataset holen, seitenweise"""
    ids: list[int] = []
    offset = 0
    limit = 100
    while True:
        url = f"{API_BASE}/api/v0/m/datasets/{dataset_id}/images/?limit={limit}&offset={offset}"
        with urllib.request.urlopen(url, timeout=15) as response:
            payload = json.load(response)
        ids.extend(image["@id"] for image in payload["data"])
        offset += limit
        if offset >= payload["meta"]["totalCount"]:
            break
    return ids


def list_projects_page(offset: int = 0, limit: int = 40) -> tuple[list[dict], int]:
    """eine seite der obersten ebene (alle idr studien) holen, fuers durchstoebern ganz
    ohne vorher schon eine id zu kennen. idr hat aktuell ~150 studien, darum auch hier
    bewusst paginiert statt alles auf einmal (gleiches prinzip wie die beiden funktionen
    weiter unten, eine ebene tiefer)"""
    url = f"{API_BASE}/api/v0/m/projects/?limit={limit}&offset={offset}"
    with urllib.request.urlopen(url, timeout=15) as response:
        payload = json.load(response)
    projects = [{"id": p["@id"], "name": p.get("Name")} for p in payload["data"]]
    return projects, payload["meta"]["totalCount"]


def list_project_datasets_page(project_id: int, offset: int = 0, limit: int = 40) -> tuple[list[dict], int]:
    """eine seite datasets (id+name) eines projekts/einer studie holen, analog zu
    list_dataset_images_page eine ebene hoeher"""
    url = f"{API_BASE}/api/v0/m/projects/{project_id}/datasets/?limit={limit}&offset={offset}"
    with urllib.request.urlopen(url, timeout=15) as response:
        payload = json.load(response)
    datasets = [{"id": d["@id"], "name": d.get("Name")} for d in payload["data"]]
    return datasets, payload["meta"]["totalCount"]


def first_dataset_image_id(dataset_id: int) -> int | None:
    """erstes bild eines datasets, fuers vorschau-thumbnail auf der dataset-ebene beim
    durchstoebern (siehe list_dataset_images_page) - None wenn das dataset leer ist"""
    images, _ = list_dataset_images_page(dataset_id, offset=0, limit=1)
    return images[0]["id"] if images else None


def first_project_preview_image_id(project_id: int) -> int | None:
    """erstes bild des ersten datasets eines projekts, fuers vorschau-thumbnail auf der
    studien-ebene - zwei hops (erstes dataset, dann dessen erstes bild), None falls das
    projekt keine datasets oder das erste dataset keine bilder hat"""
    datasets, _ = list_project_datasets_page(project_id, offset=0, limit=1)
    if not datasets:
        return None
    return first_dataset_image_id(datasets[0]["id"])


def list_dataset_images_page(dataset_id: int, offset: int = 0, limit: int = 40) -> tuple[list[dict], int]:
    """eine seite bilder (id+name) eines datasets holen, fuers browsen/scrollen im
    upload-tool. anders als list_dataset_image_ids (holt alles auf einmal, fuer den
    massen-fetch per --dataset) holt das hier bewusst immer nur eine seite - auch ein
    dataset mit tausenden bildern bleibt so browsbar ohne alles auf einmal laden zu
    muessen. gibt zusaetzlich die gesamtzahl zurueck (fuers "mehr laden")"""
    url = f"{API_BASE}/api/v0/m/datasets/{dataset_id}/images/?limit={limit}&offset={offset}"
    with urllib.request.urlopen(url, timeout=15) as response:
        payload = json.load(response)
    images = [{"id": image["@id"], "name": image.get("Name")} for image in payload["data"]]
    return images, payload["meta"]["totalCount"]


def list_project_dataset_ids(project_id: int) -> list[int]:
    """alle dataset ids in einem projekt (studie) holen, seitenweise"""
    ids: list[int] = []
    offset = 0
    limit = 100
    while True:
        url = f"{API_BASE}/api/v0/m/projects/{project_id}/datasets/?limit={limit}&offset={offset}"
        with urllib.request.urlopen(url, timeout=15) as response:
            payload = json.load(response)
        ids.extend(dataset["@id"] for dataset in payload["data"])
        offset += limit
        if offset >= payload["meta"]["totalCount"]:
            break
    return ids


def _save_thumbnail(volume, path: Path) -> None:
    """mittlerer z-schnitt als schnelle vorschau (siehe Punkt 4 der wunschliste,
    STATUS.md 25.08.) - zeigt ungefaehr wie das volumen im viewer aussehen wird, weil es
    dieselben schon entrauschten/kontrast-gestreckten daten nutzt statt idrs eigenes
    thumbnail (andere darstellung/ohne unsere pipeline). rgb (z,y,x,3) und graustufen
    (z,y,x) beide direkt speicherbar, kein extra umbau noetig"""
    import imageio.v3 as iio

    mid_z = volume.shape[0] // 2
    iio.imwrite(path, volume[mid_z])


def thumbnail_path_for(volume_path: Path) -> Path:
    """vorschau-pfad aus dem volumen-pfad ableiten (<name>_thumbnail.jpg statt fix
    'thumbnail.jpg') - kollisionsfrei falls mehrere volumen mal im selben ordner landen,
    z.b. beim adb push aufs geraet (siehe deploy_to_headset.push_to_device, das alles
    flach in persistentDataPath ablegt statt in eigenen unterordnern wie lokal)"""
    return volume_path.with_name(f"{volume_path.stem.removesuffix('.nii')}_thumbnail.jpg")


def fetch_one(image_id: int, name: str | None = None, denoise: bool = True, max_voxels: int = MAX_VOXELS) -> Path:
    """ein bild laden und konvertieren, gibt den pfad des volumens zurueck"""
    name = name or f"idr_{image_id}"
    out_dir = DATA_ROOT / str(image_id)
    out_dir.mkdir(parents=True, exist_ok=True)
    volume_path = out_dir / f"{name}.nii.gz"
    metadata_path = out_dir / "metadata.json"
    thumbnail_path = thumbnail_path_for(volume_path)

    zarr_url = resolve_zarr_url(image_id)
    print(f"[{image_id}] zarr gefunden: {zarr_url}")

    volume = convert(zarr_url, volume_path, denoise=denoise, max_voxels=max_voxels)
    print(f"[{image_id}] volumen gespeichert: {volume_path} shape={volume.shape}")

    _save_thumbnail(volume, thumbnail_path)
    print(f"[{image_id}] vorschau gespeichert: {thumbnail_path}")

    metadata = fetch_metadata(image_id)
    metadata_path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"[{image_id}] metadaten gespeichert: {metadata_path}")

    return volume_path


def push_one(volume_path: Path, package: str) -> None:
    adb = find_adb()
    check_single_device(adb)
    push_to_device(adb, volume_path, package)

    thumbnail_path = thumbnail_path_for(volume_path)
    if thumbnail_path.is_file():
        push_to_device(adb, thumbnail_path, package)

    # metadata.json liegt lokal einmal pro idr-bild-ordner (siehe fetch_one), auf dem
    # geraet landet aber alles flach im selben verzeichnis (siehe push_to_device) - mit
    # umbenennung pushen, sonst wuerden mehrere bilder sich die eine datei teilen bzw.
    # gegenseitig ueberschreiben. gleiches namensschema wie thumbnail_path_for, siehe
    # VolumeMetadataPanel.cs auf der unity seite
    metadata_path = volume_path.with_name("metadata.json")
    if metadata_path.is_file():
        remote_name = f"{volume_path.stem.removesuffix('.nii')}_metadata.json"
        push_to_device(adb, metadata_path, package, remote_name=remote_name)

    ensure_api_key_pushed(adb, package)
    print(f"[{volume_path.name}] gepusht")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("image_id", type=int, nargs="?", help="IDR image id, aus der url idr.openmicroscopy.org/webclient/img_detail/<id>")
    parser.add_argument("--dataset", type=int, help="statt einem bild alle bilder aus diesem IDR dataset laden")
    parser.add_argument("--project", type=int, help="statt einem bild alle bilder aus allen datasets dieser IDR studie laden")
    parser.add_argument("--name", help="dateiname ohne endung, nur bei einzelbild, default idr_<id>")
    parser.add_argument("--push", action="store_true", help="danach zusaetzlich aufs verbundene quest pushen")
    parser.add_argument("--package", default=DEFAULT_PACKAGE, help=f"android package id auf dem geraet, default {DEFAULT_PACKAGE}")
    parser.add_argument("--no-denoise", dest="denoise", action="store_false",
                         help="Median-Filter vor dem Kontrast-Strecken deaktivieren (zum Vergleichen)")
    parser.add_argument("--max-voxels", type=int, default=MAX_VOXELS,
                         help=f"Downsample-Schwelle in raeumlichen Voxeln, Standard {MAX_VOXELS:,} (siehe convert_to_volume.MAX_VOXELS)")
    parser.set_defaults(denoise=True)
    args = parser.parse_args()

    modes_given = sum(x is not None for x in (args.image_id, args.dataset, args.project))
    if modes_given != 1:
        parser.error("genau eins angeben: image_id, --dataset oder --project")

    if args.project is not None:
        dataset_ids = list_project_dataset_ids(args.project)
        print(f"projekt {args.project}: {len(dataset_ids)} datasets gefunden")
        image_ids = []
        for dataset_id in dataset_ids:
            ids = list_dataset_image_ids(dataset_id)
            print(f"dataset {dataset_id}: {len(ids)} bilder gefunden")
            image_ids.extend(ids)
    elif args.dataset is not None:
        image_ids = list_dataset_image_ids(args.dataset)
        print(f"dataset {args.dataset}: {len(image_ids)} bilder gefunden")
    else:
        image_ids = [args.image_id]

    single_image_mode = args.image_id is not None
    volume_paths = []
    failed_ids = []
    for image_id in image_ids:
        try:
            volume_paths.append(fetch_one(image_id, name=args.name if single_image_mode else None, denoise=args.denoise, max_voxels=args.max_voxels))
        except Exception as error:
            print(f"[{image_id}] fehlgeschlagen, weiter mit dem naechsten: {error}")
            failed_ids.append(image_id)

    if failed_ids:
        print(f"fehlgeschlagen: {failed_ids}")

    if not args.push:
        return

    for volume_path in volume_paths:
        try:
            push_one(volume_path, args.package)
        except Exception as error:
            print(f"[{volume_path.name}] push fehlgeschlagen: {error}")


if __name__ == "__main__":
    main()
