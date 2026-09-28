"""bild konvertieren und direkt aufs quest pushen.

volumeview braucht auf android persistentDataPath statt windows pfad
konvertiert und adb push in einem rutsch statt von hand

usage:
    python deploy_to_headset.py input --name my_sample
    python deploy_to_headset.py data/sample/idr_6001240.nii.gz --skip-convert
    python deploy_to_headset.py input --package com.DefaultCompany.BioimageVR

quest muss per usb debugging verbunden sein, adb devices zeigt genau ein geraet
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

from convert_to_volume import convert

DEFAULT_PACKAGE = "com.DefaultCompany.BioimageVR"
SAMPLE_DIR = Path(__file__).resolve().parent.parent / "data" / "sample"
API_KEY_FILENAME = ".scadsai-api-key"

# kein expliziter package name in player settings gesetzt
# unity leitet ihn aus companyname und productname ab
# bei aenderung dort --package mitgeben


def find_adb() -> str:
    """adb suchen, erst path dann unity bundle"""
    on_path = shutil.which("adb")
    if on_path:
        return on_path

    for hub_root in (Path(r"C:\Program Files\Unity\Hub\Editor"),):
        if not hub_root.is_dir():
            continue
        for editor_dir in hub_root.iterdir():
            candidate = editor_dir / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK" / "platform-tools" / "adb.exe"
            if candidate.is_file():
                return str(candidate)

    raise FileNotFoundError(
        "adb.exe not found on PATH or under a Unity Hub install. Install Android "
        "platform-tools or pass its path via --adb."
    )


def check_single_device(adb: str) -> None:
    result = subprocess.run([adb, "devices"], capture_output=True, text=True, check=True)
    lines = [l for l in result.stdout.splitlines()[1:] if l.strip()]
    if not lines:
        raise RuntimeError(
            "No device found (`adb devices` is empty). Connect the Quest via USB, "
            "accept the 'Allow USB debugging' prompt on the headset, and retry."
        )
    if len(lines) > 1:
        raise RuntimeError(f"Multiple devices found, expected one:\n{result.stdout}")
    if "unauthorized" in lines[0]:
        raise RuntimeError(
            "Device is unauthorized - put the headset on and accept the 'Allow USB "
            "debugging' prompt, then retry."
        )


def push_to_device(adb: str, local_path: Path, package: str, remote_name: str | None = None) -> None:
    remote_dir = f"/sdcard/Android/data/{package}/files"
    remote_path = f"{remote_dir}/{remote_name or local_path.name}"

    subprocess.run([adb, "shell", "mkdir", "-p", remote_dir], check=True)
    subprocess.run([adb, "push", str(local_path), remote_path], check=True)
    print(f"Pushed {local_path} -> {remote_path}")


def ensure_api_key_pushed(adb: str, package: str) -> None:
    """api key auch pushen, sonst schlaegt chat und voice auf dem geraet fehl"""
    remote_dir = f"/sdcard/Android/data/{package}/files"
    remote_path = f"{remote_dir}/{API_KEY_FILENAME}"

    check = subprocess.run([adb, "shell", "test", "-f", remote_path], capture_output=True)
    if check.returncode == 0:
        return  # schon auf dem geraet

    local_key = Path.home() / API_KEY_FILENAME
    if not local_key.is_file():
        print(f"Warning: no {local_key} found locally and none on the device either - "
              "VLM/STT chat will fail until one is pushed (see unity/README.md).")
        return

    subprocess.run([adb, "shell", "mkdir", "-p", remote_dir], check=True)
    subprocess.run([adb, "push", str(local_key), remote_path], check=True)
    print(f"Pushed API key -> {remote_path} (wasn't on the device yet).")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input_path", help="2D slice folder, a single stack file, a remote URL, or (with --skip-convert) an existing .nii.gz")
    parser.add_argument("--name", help="Output filename stem (default: derived from input_path)")
    parser.add_argument("--skip-convert", action="store_true", help="input_path is already a .nii.gz - push as-is instead of converting")
    parser.add_argument("--package", default=DEFAULT_PACKAGE, help=f"Android application id on the device (default: {DEFAULT_PACKAGE})")
    parser.add_argument("--adb", help="Path to adb.exe (default: auto-detect)")
    parser.add_argument("--no-push", action="store_true", help="Only convert, don't push to a device (e.g. no headset connected right now)")
    args = parser.parse_args()

    if args.skip_convert:
        output_path = Path(args.input_path)
        if not output_path.is_file():
            sys.exit(f"--skip-convert given but '{output_path}' doesn't exist.")
    else:
        name = args.name or Path(args.input_path.rstrip("/")).stem or "sample"
        output_path = SAMPLE_DIR / f"{name}.nii.gz"
        volume = convert(args.input_path, output_path)
        print(f"Converted {args.input_path} -> {output_path} (shape={volume.shape})")

    if args.no_push:
        return

    adb = args.adb or find_adb()
    check_single_device(adb)
    push_to_device(adb, output_path, args.package)

    thumbnail_path = output_path.with_name(f"{output_path.stem.removesuffix('.nii')}_thumbnail.jpg")
    if thumbnail_path.is_file():
        push_to_device(adb, thumbnail_path, args.package)

    metadata_path = output_path.with_name("metadata.json")
    if metadata_path.is_file():
        remote_name = f"{output_path.stem.removesuffix('.nii')}_metadata.json"
        push_to_device(adb, metadata_path, args.package, remote_name=remote_name)

    ensure_api_key_pushed(adb, args.package)
    print(
        "Done. On the headset: Volume File Path only needs to match the filename "
        f"'{output_path.name}' (VolumeView.cs resolves it under persistentDataPath "
        "automatically on Android) - if this is a new file, update the 'Volume File "
        "Path' field on the Volume GameObject in Assets/Scenes/Phase1.unity before rebuilding."
    )


if __name__ == "__main__":
    main()
