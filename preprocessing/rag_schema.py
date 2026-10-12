# zielschema fuer rag chunks, mehrere kleine chunks je bild, ein adapter je quelle

from __future__ import annotations

FIELD_OVERVIEW = "overview"
FIELD_PROTOCOL = "protocol"
FIELD_SEGMENTATION = "segmentation"
FIELD_PHENOTYPE = "phenotype"
FIELD_GENOTYPE = "genotype"
FIELD_RESOLUTION = "resolution"

# beschreibungen der kategorien fuer den llm klassifizierer
FIELD_DESCRIPTIONS = {
    FIELD_OVERVIEW: "Kurzer Ueberblick was auf dem Bild zu sehen ist, z.b. Organismus, Praeparat, Zelllinie, Gewebeteil",
    FIELD_PROTOCOL: "Wie die Probe hergestellt, praepariert, gefaerbt oder aufgenommen wurde: Zucht, Faerbung, Antikoerper, Mikroskop, Objektiv, Bildverarbeitung",
    FIELD_SEGMENTATION: "Wie/womit das Bild segmentiert wurde: Methode, Software, segmentierter Kanal",
    FIELD_PHENOTYPE: "Beobachteter Phaenotyp oder untersuchtes biologisches Merkmal",
    FIELD_GENOTYPE: "Genotyp, genetischer Hintergrund oder Mutation der Probe",
    FIELD_RESOLUTION: "Aufloesung bzw Voxelgroesse (kommt normalerweise strukturiert aus den Pixeldaten, nicht aus key_values)",
}


def make_chunk(source_db: str, source_id: int | str, field_type: str, text: str, index: int = 0) -> dict:
    # ein chunk, id kollisionssicher auch bei mehreren gleichen typs
    return {
        "id": f"{source_db}:{source_id}:{field_type}:{index}",
        "source_db": source_db,
        "source_id": str(source_id),
        "field_type": field_type,
        "text": text,
    }
