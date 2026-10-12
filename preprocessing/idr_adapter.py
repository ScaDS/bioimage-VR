# idr metadaten auf chunks aus rag_schema.py, key_values klassifiziert das llm

from __future__ import annotations

from collections import defaultdict

from llm_chunker import classify_key_values
from rag_schema import FIELD_OVERVIEW, FIELD_RESOLUTION, make_chunk

SOURCE_DB = "idr"


def map_idr_metadata(raw: dict) -> list[dict]:
    # raw ist das ergebnis von fetch_metadata, fehlende felder geben einfach weniger chunks
    image_id = raw["image_id"]
    key_values = raw.get("key_values") or {}
    chunks = []

    structured_overview = _structured_overview_text(raw)
    if structured_overview:
        chunks.append(make_chunk(SOURCE_DB, image_id, FIELD_OVERVIEW, structured_overview, index=0))

    resolution_text = _resolution_text(raw.get("voxel_size_um") or {})
    if resolution_text:
        chunks.append(make_chunk(SOURCE_DB, image_id, FIELD_RESOLUTION, resolution_text))

    by_field_type = defaultdict(list)
    for classified in classify_key_values(key_values):
        by_field_type[classified["field_type"]].append(classified["text"])
    for field_type, texts in by_field_type.items():
        start_index = 1 if field_type == FIELD_OVERVIEW else 0
        for offset, text in enumerate(texts):
            chunks.append(make_chunk(SOURCE_DB, image_id, field_type, text, index=start_index + offset))

    return chunks


def _structured_overview_text(raw: dict) -> str:
    # overview teil aus strukturierten pixeldaten, zuverlaessiger als key_values
    parts = []
    name = raw.get("name")
    if name:
        parts.append(f"Bild: {name}.")
    channel_names = [c for c in (raw.get("channels") or []) if c]
    if channel_names:
        parts.append(f"Kanaele/Marker: {', '.join(channel_names)}.")
    return " ".join(parts)


def _resolution_text(voxel_size_um: dict) -> str:
    values = [voxel_size_um.get(axis) for axis in ("x", "y", "z")]
    present = [v for v in values if v is not None]
    if not present:
        return ""
    dims = " x ".join(f"{v:.2f}" for v in present)
    return f"Aufloesung: {dims} µm/Voxel."
