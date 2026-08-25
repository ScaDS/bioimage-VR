"""idr metadaten (aus fetch_from_idr.fetch_metadata bzw metadata.json) auf die chunk
liste aus rag_schema.py mappen

key_values sind idrs freie map annotationen, feldnamen variieren zwischen studien -
statt fester kandidaten keys pro studie klassifiziert llm_chunker.classify_key_values
sie automatisch in die FIELD_* kategorien, macht diesen adapter unabhaengig von
konkreten idr feldnamen. size/channels/voxel_size_um kommen dagegen strukturiert
direkt aus den pixel daten und bleiben deshalb reiner code, kein llm noetig
"""

from __future__ import annotations

from collections import defaultdict

from llm_chunker import classify_key_values
from rag_schema import FIELD_OVERVIEW, FIELD_RESOLUTION, make_chunk

SOURCE_DB = "idr"


def map_idr_metadata(raw: dict) -> list[dict]:
    """raw = fetch_from_idr.fetch_metadata() ergebnis bzw eine geladene metadata.json
    ein bild wird zu mehreren chunks, bilder ohne bestimmte felder erzeugen einfach
    weniger chunks statt platzhaltern"""
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
    """teil des overview chunks der aus strukturierten pixeldaten kommt (name,
    channels), zuverlaessiger als jedes key_values feld - organismus etc kommt
    separat ueber classify_key_values, da das aus key_values stammt"""
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
