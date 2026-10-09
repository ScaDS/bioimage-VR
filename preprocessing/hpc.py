"""rechenjobs vom pc auf den leipziger cluster schicken und das ergebnis zurueckholen.

der pc ist der vermittler: die brille spricht nie selbst mit dem cluster, dort braucht es
ssh mit schluessel und die rechenknoten sind von aussen nicht erreichbar

voraussetzung: `ssh leipzig-hpc` geht ohne passwort (eintrag in ~/.ssh/config), anderer
name ueber BIOIMAGE_VR_HPC_HOST. auf dem cluster liegt alles im workspace "bioimage-vr"
(pfad ueber ws_find bioimage-vr), das home ist dafuer zu klein:
    env/       python umgebung mit cellpose, einmal angelegt
    models/    gewichte von Cellpose-SAM, die rechenknoten laden nichts aus dem netz
    scripts/   segment_cells.py, wird bei jedem job frisch hochgeladen
    jobs/      ein ordner pro job mit eingabe, maske und slurm.log
workspaces laufen nach 30 tagen ab, verlaengern mit `ws_allocate -x bioimage-vr 30`,
sonst ist die umgebung weg und muss neu angelegt werden

bisher ein job typ: zellen bzw. kerne mit Cellpose-SAM, siehe segmentation/segment_cells.py

usage:
    python hpc.py cells data/idr/6001240/idr_6001240_denoised.nii.gz
    python hpc.py cells volume.nii.gz --push
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import nibabel as nib
import numpy as np

from convert_labels import labels_path_for, write_labels_array
from segment_anatomy import metadata_path_for, push, read_voxel_size

HOST = os.environ.get("BIOIMAGE_VR_HPC_HOST", "leipzig-hpc")
WORKSPACE = "bioimage-vr"
PYTHON_MODULE = "Python/3.10.8-GCCcore-12.2.0"
# mehrere partitionen, slurm nimmt die auf der zuerst eine karte frei ist
GPU_PARTITIONS = "gpu-a30,gpu-l40s,gpu-v100,gpu-rtx2080ti"
CELLS_SCRIPT = Path(__file__).resolve().parent.parent / "segmentation" / "segment_cells.py"
SSH_OPTIONS = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=20", "-o", "LogLevel=ERROR"]

JOB_TEMPLATE = """#!/bin/bash
#SBATCH --job-name=bvr-cells
#SBATCH --partition={partitions}
#SBATCH --gres=gpu:1
#SBATCH --cpus-per-task=4
#SBATCH --mem=32G
#SBATCH --time={time_limit}
#SBATCH --output=slurm.log
module load {python_module}
source {root}/env/bin/activate
export CELLPOSE_LOCAL_MODELS_PATH={root}/models
python {root}/scripts/segment_cells.py image.tif masks.tif {extra}
"""


def ssh(command: str, check: bool = True) -> str:
    result = subprocess.run(["ssh", *SSH_OPTIONS, HOST, command], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"ssh {HOST}: {command}\n{result.stderr.strip() or result.stdout.strip()}")
    return result.stdout.strip()


def remote_root() -> str:
    """pfad des workspace auf dem cluster"""
    root = ssh(f"ws_find {WORKSPACE}", check=False)
    if not root.startswith("/"):
        raise RuntimeError(f"No workspace '{WORKSPACE}' on {HOST}. It expires after 30 days, "
                           "set it up again as described at the top of hpc.py.")
    return root


def upload(local: Path, remote: str) -> None:
    subprocess.run(["scp", *SSH_OPTIONS, str(local), f"{HOST}:{remote}"], check=True, capture_output=True)


def download(remote: str, local: Path) -> None:
    subprocess.run(["scp", *SSH_OPTIONS, f"{HOST}:{remote}", str(local)], check=True, capture_output=True)


def job_state(job_id: str) -> str:
    """PENDING, RUNNING, COMPLETED, FAILED usw."""
    state = ssh(f"squeue -h -j {job_id} -o %T", check=False)
    if state:
        return state.splitlines()[0].strip()
    # nicht mehr in der schlange, dann steht der ausgang in der abrechnung
    state = ssh(f"sacct -n -X -j {job_id} -o State", check=False)
    return state.split()[0].strip("+") if state else "UNKNOWN"


def wait_for(job_id: str, timeout: float = 3600, poll: float = 15, report=print) -> str:
    start = time.time()
    last = ""
    while True:
        state = job_state(job_id)
        if state != last:
            report(f"  job {job_id}: {state.lower()} after {time.time() - start:.0f} s")
            last = state
        if state not in ("PENDING", "CONFIGURING", "RUNNING", "COMPLETING", "UNKNOWN"):
            return state
        if time.time() - start > timeout:
            raise TimeoutError(f"job {job_id} still {state} after {timeout:.0f} s, it keeps running on the cluster")
        time.sleep(poll)


def volume_to_czyx(volume_path: Path) -> np.ndarray:
    """konvertiertes volumen (x,y,z) bzw. (x,y,z,3) nach (c,z,y,x), leere kanaele fallen weg.
    die maske passt dadurch voxelgenau auf das was die brille zeigt"""
    data = np.asanyarray(nib.load(str(volume_path)).dataobj)
    if data.dtype.names:
        data = np.stack([data[name] for name in data.dtype.names], axis=-1)
    if data.ndim == 3:
        return np.ascontiguousarray(data.transpose(2, 1, 0))[None]
    channels = [data[..., c].transpose(2, 1, 0) for c in range(data.shape[3]) if data[..., c].any()]
    return np.ascontiguousarray(np.stack(channels))


def anisotropy_for(volume_path: Path) -> float | None:
    """z voxelgroesse durch xy, None wenn die metadaten fehlen"""
    metadata_path = metadata_path_for(volume_path)
    if not metadata_path:
        return None
    voxel, _ = read_voxel_size(json.loads(metadata_path.read_text(encoding="utf-8")))
    if voxel is None:
        return None
    return float(voxel[2] / ((voxel[0] + voxel[1]) / 2.0))


# darunter gelten die voxel als wuerfel, wie im notebook
ANISOTROPY_FROM = 1.5


def cellpose_anisotropy(volume_path: Path) -> float | None:
    """anisotropie nur weitergeben wenn sie deutlich ist.
    blastozyste 08.10. (z zu xy 1.39): ohne 62 von 63 kernen getroffen, mit 61, kein gewinn.
    staerker gestauchte stapel sind noch nicht geprueft"""
    value = anisotropy_for(volume_path)
    return value if value and value >= ANISOTROPY_FROM else None


def segment_cells(volume_path: Path, anisotropy: float | None = None, time_limit: str = "00:30:00",
                  report=print) -> tuple[np.ndarray, dict]:
    """volumen hochladen, Cellpose-SAM als gpu job, maske (z,y,x) und kennzahlen zurueck"""
    import tifffile

    image = volume_to_czyx(volume_path)
    stem = volume_path.name.removesuffix(".gz").removesuffix(".nii")
    root = remote_root()
    job_dir = f"{root}/jobs/{stem}_{time.strftime('%Y%m%d_%H%M%S')}"
    extra = f"--anisotropy {anisotropy:.4f}" if anisotropy else ""

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        tifffile.imwrite(str(tmp_path / "image.tif"), image, metadata={"axes": "CZYX"})
        (tmp_path / "job.sh").write_text(JOB_TEMPLATE.format(
            partitions=GPU_PARTITIONS, time_limit=time_limit, python_module=PYTHON_MODULE,
            root=root, extra=extra), encoding="utf-8", newline="\n")

        report(f"  uploading {image.shape} to {HOST}:{job_dir}")
        ssh(f"mkdir -p {job_dir} {root}/scripts")
        upload(CELLS_SCRIPT, f"{root}/scripts/segment_cells.py")
        upload(tmp_path / "image.tif", f"{job_dir}/image.tif")
        upload(tmp_path / "job.sh", f"{job_dir}/job.sh")

        job_id = ssh(f"cd {job_dir} && sbatch --parsable job.sh").split(";")[0].strip()
        state = wait_for(job_id, report=report)
        if state != "COMPLETED":
            log = ssh(f"tail -n 25 {job_dir}/slurm.log", check=False)
            raise RuntimeError(f"job {job_id} ended as {state}\n{log}")

        download(f"{job_dir}/masks.tif", tmp_path / "masks.tif")
        download(f"{job_dir}/masks.json", tmp_path / "masks.json")
        masks = tifffile.imread(str(tmp_path / "masks.tif"))
        summary = json.loads((tmp_path / "masks.json").read_text())
    summary.update({"job_id": job_id, "job_dir": job_dir})
    return masks, summary


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("job", choices=["cells"])
    parser.add_argument("volume_path", type=Path, help="The converted .nii.gz")
    parser.add_argument("--overwrite", action="store_true", help="Replace an existing _labels file")
    parser.add_argument("--push", action="store_true", help="Also adb push the mask to the headset")
    args = parser.parse_args()

    if not args.volume_path.is_file():
        sys.exit(f"Volume '{args.volume_path}' not found.")
    if labels_path_for(args.volume_path).exists() and not args.overwrite:
        sys.exit(f"{labels_path_for(args.volume_path)} already exists, pass --overwrite to replace it.")

    masks, summary = segment_cells(args.volume_path, cellpose_anisotropy(args.volume_path))
    print(f"{summary['cells']} cells in {summary['seconds']} s on the cluster")
    labels_path = write_labels_array(masks, args.volume_path)
    if args.push:
        push([labels_path])


if __name__ == "__main__":
    main()
