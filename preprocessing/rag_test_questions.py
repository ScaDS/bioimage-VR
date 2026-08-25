"""testfragen fuers retrieval, jeweils bezogen auf ein konkret geladenes bild - kein
automatisches scoring, ausgabe zum gegenlesen. siehe plan fuer die begruendung der fragen

lauf: python preprocessing/rag_test_questions.py
"""

from __future__ import annotations

from rag_index import query

QUESTIONS = [
    # (aktuell geladenes bild, frage, worauf beim gegenlesen achten)
    ("6001240", "Wie wurde diese Probe hergestellt?", "protocol (Zuchtprotokoll)"),
    ("6001240", "Welche Faerbung wurde verwendet?", "protocol (Immunfaerbung) oder overview (Kanaele)"),
    ("6001240", "Mit welchem Mikroskop wurde das aufgenommen?", "protocol (Sp8 Leica confocal)"),
    ("6001240", "Wie wurde das Bild segmentiert?", "segmentation (Nessys Editor)"),
    ("6001240", "Welcher Organismus ist zu sehen?", "overview (Mus musculus)"),
    ("6001240", "Wie gross ist ein Voxel in diesem Bild?", "resolution"),
    ("6001240", "Gibt es andere Datensaetze mit aehnlicher LaminB1 Faerbung?", "ueber den index: andere 6001237-6001258 bilder"),
    ("1884807", "Was fuer ein Phaenotyp wird hier untersucht?", "phenotype (centrosome)"),
    ("1884807", "Welches Gen/Protein steht im Fokus?", "grenzfall, evtl kein guter treffer"),
]


def run() -> None:
    for current_image_id, question, expectation in QUESTIONS:
        print(f'\n=== Bild {current_image_id}: "{question}" ===')
        print(f"erwartet: {expectation}")

        print("-- eigene chunks --")
        for hit in query(question, n_results=3, source_id=current_image_id):
            field_type = hit["metadata"]["field_type"]
            print(f"  [{field_type}] {round(hit['distance'], 3)}  {hit['text'][:90]}")

        print("-- ueber den ganzen index --")
        for hit in query(question, n_results=4):
            if hit["metadata"]["source_id"] == current_image_id:
                continue
            source_id = hit["metadata"]["source_id"]
            field_type = hit["metadata"]["field_type"]
            print(f"  [{source_id}/{field_type}] {round(hit['distance'], 3)}  {hit['text'][:90]}")


if __name__ == "__main__":
    run()
