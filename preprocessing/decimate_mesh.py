"""grosse meshes (z.b. connectomics-neuronen von microns-explorer.org, ~1-2 mio
dreiecke) auf eine headset-taugliche dreieckszahl reduzieren, bevor sie in unity
geladen werden

nur die dreiecksreduktion - koordinaten bleiben unveraendert, keine
zentrierung/skalierung hier (das macht PlyMeshLoader.cs zur laufzeit, gleiches
prinzip wie VolumeView.cs beim volumen: rohdaten bleiben rohdaten, unity
normalisiert beim laden)

lauf: python decimate_mesh.py pfad/zum/mesh.ply --target-triangles 200000
schreibt pfad/zum/mesh_decimated.ply
"""

from __future__ import annotations

import argparse
from pathlib import Path

import trimesh


def decimate(input_path: Path, target_triangles: int, output_path: Path | None = None) -> Path:
    mesh = trimesh.load(str(input_path), process=False)
    if not isinstance(mesh, trimesh.Trimesh):
        raise ValueError(f"{input_path} ist kein dreiecksmesh (geladen als {type(mesh).__name__})")

    original_triangles = len(mesh.faces)
    if original_triangles <= target_triangles:
        print(f"{input_path.name}: hat bereits nur {original_triangles} dreiecke, keine reduktion noetig")
        simplified = mesh
    else:
        simplified = mesh.simplify_quadric_decimation(face_count=target_triangles)

    if output_path is None:
        output_path = input_path.with_name(f"{input_path.stem}_decimated.ply")
    simplified.export(str(output_path))

    print(f"{input_path.name}: {original_triangles} -> {len(simplified.faces)} dreiecke, "
          f"{len(mesh.vertices)} -> {len(simplified.vertices)} vertices -> {output_path}")
    return output_path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="pfad zum .ply mesh")
    parser.add_argument("--target-triangles", type=int, default=200_000,
                         help="ziel-dreieckszahl nach der reduktion (default 200000, headset-tauglich)")
    parser.add_argument("--output", type=Path, default=None, help="ausgabepfad (default: <input>_decimated.ply)")
    args = parser.parse_args()

    decimate(args.input, args.target_triangles, args.output)


if __name__ == "__main__":
    main()
