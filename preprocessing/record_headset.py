"""video vom quest bildschirm aufnehmen, ueber adb screenrecord.

android deckelt eine einzelne screenrecord aufnahme bei 180s, darum hier in
segmenten aufgenommen und danach mit ffmpeg zusammengefuegt (falls installiert)

usage:
    python record_headset.py --out session1.mp4
    (ctrl+c zum stoppen)
    python record_headset.py --out session1.mp4 --duration 300
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from deploy_to_headset import find_adb, check_single_device

REMOTE_DIR = "/sdcard/bioimagevr_capture"
SEGMENT_LIMIT_S = 170  # unter dem harten 180s android limit bleiben


def record_segments(adb: str, duration: float | None) -> list[str]:
    subprocess.run([adb, "shell", "mkdir", "-p", REMOTE_DIR], check=True)

    remote_segments: list[str] = []
    seg_index = 0
    start = time.monotonic()

    while True:
        elapsed = time.monotonic() - start
        if duration is not None and elapsed >= duration:
            break
        time_limit = SEGMENT_LIMIT_S
        if duration is not None:
            time_limit = min(SEGMENT_LIMIT_S, int(duration - elapsed) + 1)

        remote_seg = f"{REMOTE_DIR}/seg_{seg_index:03d}.mp4"
        print(f"Aufnahme laeuft (Segment {seg_index}, max {time_limit}s) - Ctrl+C zum Stoppen")
        proc = subprocess.Popen([adb, "shell", "screenrecord", "--time-limit", str(time_limit), remote_seg])

        try:
            proc.wait()
        except KeyboardInterrupt:
            # sigint auf dem geraet schicken statt hier lokal zu killen
            # sonst wird die mp4 nicht sauber abgeschlossen und ist unbrauchbar
            subprocess.run([adb, "shell", "pkill", "-INT", "screenrecord"])
            proc.wait()
            remote_segments.append(remote_seg)
            return remote_segments

        remote_segments.append(remote_seg)
        seg_index += 1

    return remote_segments


def pull_segments(adb: str, remote_segments: list[str], local_dir: Path) -> list[Path]:
    local_paths = []
    for remote_seg in remote_segments:
        local_path = local_dir / Path(remote_seg).name
        subprocess.run([adb, "pull", remote_seg, str(local_path)], check=True)
        local_paths.append(local_path)
    return local_paths


def cleanup_remote(adb: str, remote_segments: list[str]) -> None:
    for remote_seg in remote_segments:
        subprocess.run([adb, "shell", "rm", remote_seg], check=False)


def stitch(local_paths: list[Path], out_path: Path) -> bool:
    """segmente zusammenfuegen, gibt false zurueck wenn ffmpeg fehlt"""
    if len(local_paths) == 1:
        shutil.move(str(local_paths[0]), out_path)
        return True

    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        return False

    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as f:
        for p in local_paths:
            f.write(f"file '{p.as_posix()}'\n")
        list_file = f.name

    subprocess.run(
        [ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", list_file, "-c", "copy", str(out_path)],
        check=True,
    )
    Path(list_file).unlink()
    for p in local_paths:
        p.unlink()
    return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True, help="Lokaler Pfad fuer die fertige Video-Datei")
    parser.add_argument("--duration", type=float, default=None, help="Aufnahmedauer in Sekunden (Default: bis Ctrl+C)")
    parser.add_argument("--adb", help="Pfad zu adb.exe (Default: auto-detect)")
    parser.add_argument("--keep-on-device", action="store_true", help="Segmente auf dem Geraet nicht loeschen")
    args = parser.parse_args()

    out_path = Path(args.out)
    adb = args.adb or find_adb()
    check_single_device(adb)

    remote_segments = record_segments(adb, args.duration)
    if not remote_segments:
        sys.exit("Keine Aufnahme erzeugt.")

    with tempfile.TemporaryDirectory() as tmp:
        local_paths = pull_segments(adb, remote_segments, Path(tmp))
        stitched = stitch(local_paths, out_path)

    if not args.keep_on_device:
        cleanup_remote(adb, remote_segments)

    if stitched:
        print(f"Fertig: {out_path}")
    else:
        print(
            f"ffmpeg nicht gefunden, {len(local_paths)} Segmente liegen unter {out_path.parent} "
            "(seg_000.mp4 usw.) - manuell zusammenfuegen oder ffmpeg installieren und erneut laufen lassen."
        )


if __name__ == "__main__":
    main()
