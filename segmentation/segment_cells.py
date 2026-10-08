"""zellen bzw. kerne in einem 3d mikroskopiebild segmentieren, Cellpose-SAM.

skript fassung von Segment_Blastocyste.ipynb, gleiche einstellungen: CellposeModel mit gpu,
do_3D, alle kanaele gehen rein. laeuft auf dem cluster als job, siehe preprocessing/hpc.py,
geht aber auch von hand

eingabe  tif als (c,z,y,x) oder (z,y,x)
ausgabe  tif als (z,y,x) uint16, 0 hintergrund, 1..n je eine zelle

usage:
    python segment_cells.py image.tif masks.tif
    python segment_cells.py image.tif masks.tif --anisotropy 1.39
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np
import tifffile


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("image", type=Path)
    parser.add_argument("masks", type=Path)
    parser.add_argument("--anisotropy", type=float, default=None,
                        help="z voxel size divided by xy voxel size, leave out to treat voxels as cubes like the notebook")
    parser.add_argument("--cpu", action="store_true", help="Run without a GPU, very slow in 3D")
    args = parser.parse_args()

    from cellpose import core, models

    image = tifffile.imread(str(args.image))
    if image.ndim == 3:
        image = image[None]
    if image.ndim != 4:
        raise SystemExit(f"Expected (c,z,y,x) or (z,y,x), got shape {image.shape}")

    use_gpu = not args.cpu and core.use_gpu()
    if not use_gpu and not args.cpu:
        raise SystemExit("No GPU visible. Pass --cpu to run anyway.")

    model = models.CellposeModel(gpu=use_gpu)
    start = time.time()
    options = {"do_3D": True, "channel_axis": 0, "z_axis": 1}
    if args.anisotropy:
        options["anisotropy"] = args.anisotropy
    masks, _, _ = model.eval(image, **options)
    seconds = time.time() - start

    args.masks.parent.mkdir(parents=True, exist_ok=True)
    tifffile.imwrite(str(args.masks), masks.astype(np.uint16), metadata={"axes": "ZYX"})
    summary = {
        "input_shape": list(image.shape),
        "mask_shape": list(masks.shape),
        "cells": int(masks.max()),
        "seconds": round(seconds, 1),
        "gpu": bool(use_gpu),
        "anisotropy": args.anisotropy,
        "model": str(model.pretrained_model),
    }
    args.masks.with_suffix(".json").write_text(json.dumps(summary, indent=1))
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
