"""hard facts direkt aus rohen idr metadaten extrahieren, mit festen kandidaten keys -
bewusst UNABHAENGIG von llm_chunker.py: das ist die referenz zum gegenpruefen ob die
rag pipeline (inkl llm klassifizierung) am ende die richtige antwort liefert. wuerde
die ground truth selbst ueber den llm klassifizierer laufen, waere der test zirkulaer

nur felder mit eindeutig richtig/falsch pruefbarem wert (kein fliesstext) - siehe
rag_eval.py fuers eigentliche durchfragen+grading
"""

from __future__ import annotations

FACT_ORGANISM = "organism"
FACT_GENOTYPE = "genotype"
FACT_SEGMENTATION_METHOD = "segmentation_method"
FACT_SEGMENTED_CHANNEL = "segmented_channel"
FACT_RESOLUTION = "resolution"

QUESTIONS = {
    FACT_ORGANISM: "Welcher Organismus ist auf diesem Bild zu sehen?",
    FACT_GENOTYPE: "Welcher Genotyp liegt bei dieser Probe vor?",
    FACT_SEGMENTATION_METHOD: "Wie wurde dieses Bild segmentiert?",
    FACT_SEGMENTED_CHANNEL: "Welcher Kanal wurde in diesem Bild segmentiert?",
    FACT_RESOLUTION: "Wie gross ist ein Voxel in diesem Bild, in Mikrometern?",
}


def extract_hard_facts(raw: dict) -> dict[str, str]:
    """raw = geladene metadata.json. nur felder die tatsaechlich vorhanden sind,
    keine platzhalter - ein bild ohne Genotype feld erzeugt einfach keine
    genotype frage statt einer unbeantwortbaren"""
    key_values = raw.get("key_values") or {}
    facts = {}

    organism = key_values.get("Organism")
    if organism:
        facts[FACT_ORGANISM] = organism

    genotype = key_values.get("Genotype")
    if genotype:
        facts[FACT_GENOTYPE] = genotype

    method = key_values.get("Segmentation Method")
    if method:
        facts[FACT_SEGMENTATION_METHOD] = method

    channel = key_values.get("Segmented Channel")
    if channel:
        facts[FACT_SEGMENTED_CHANNEL] = channel

    voxel = raw.get("voxel_size_um") or {}
    values = [voxel.get(axis) for axis in ("x", "y", "z")]
    present = [v for v in values if v is not None]
    if present:
        facts[FACT_RESOLUTION] = ", ".join(f"{v:.2f}" for v in present)

    return facts
