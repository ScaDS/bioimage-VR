"""baut/aktualisiert den rag index aus allen lokal vorhandenen idr metadaten

lauf: python preprocessing/build_rag_index.py
idempotent (id-basiertes upsert in rag_index.py), gefahrlos erneut ausfuehrbar sobald
neue bilder unter data/idr/ dazukommen
"""

from __future__ import annotations

import json

from fetch_from_idr import DATA_ROOT
from idr_adapter import map_idr_metadata
from rag_index import upsert_chunks


def build() -> None:
    total_images = 0
    total_chunks = 0
    skipped = 0

    for image_dir in sorted(DATA_ROOT.iterdir()):
        if not image_dir.is_dir():
            continue
        metadata_path = image_dir / "metadata.json"
        if not metadata_path.is_file():
            skipped += 1
            continue

        raw = json.loads(metadata_path.read_text(encoding="utf-8"))
        chunks = map_idr_metadata(raw)
        upsert_chunks(chunks)
        total_images += 1
        total_chunks += len(chunks)
        print(f"[{image_dir.name}] {len(chunks)} chunks indexiert")

    print(f"fertig: {total_images} bilder, {total_chunks} chunks, {skipped} ohne metadata.json uebersprungen")


if __name__ == "__main__":
    build()
