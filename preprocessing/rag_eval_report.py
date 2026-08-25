"""liest data/rag_eval_results.csv (siehe rag_eval.py) und baut eine kurze
auswertung: balkendiagramm trefferquote pro fakt-typ + liste der falschen
antworten zum gegenlesen

lauf: python rag_eval_report.py
schreibt data/rag_eval_report.png
"""

from __future__ import annotations

import csv
from collections import defaultdict
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

CSV_PATH = Path(__file__).resolve().parent.parent / "data" / "rag_eval_results.csv"
PNG_PATH = CSV_PATH.with_name("rag_eval_report.png")
HEATMAP_PATH = CSV_PATH.with_name("rag_eval_heatmap.png")

# stand: dataviz skill referenzpalette, status.good/warning/critical
COLOR_GOOD = "#0ca30c"
COLOR_WARNING = "#fab219"
COLOR_CRITICAL = "#d03b3b"
COLOR_INK = "#0b0b0b"
COLOR_MUTED = "#898781"
COLOR_GRID = "#e1e0d9"
COLOR_SURFACE = "#fcfcfb"

FACT_LABELS = {
    "organism": "Organismus",
    "genotype": "Genotyp",
    "segmentation_method": "Segmentierungsmethode",
    "segmented_channel": "Segmentierter Kanal",
    "resolution": "Aufloesung",
}


def _status_color(accuracy: float) -> str:
    if accuracy >= 0.8:
        return COLOR_GOOD
    if accuracy >= 0.5:
        return COLOR_WARNING
    return COLOR_CRITICAL


def load_rows() -> list[dict]:
    with open(CSV_PATH, encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    for row in rows:
        row["correct"] = row["correct"] == "True"
    return rows


def build_chart(rows: list[dict]) -> None:
    by_fact = defaultdict(list)
    for row in rows:
        by_fact[row["fact_type"]].append(row["correct"])

    fact_types = sorted(by_fact, key=lambda ft: FACT_LABELS.get(ft, ft))
    accuracies = [sum(by_fact[ft]) / len(by_fact[ft]) for ft in fact_types]
    counts = [len(by_fact[ft]) for ft in fact_types]
    colors = [_status_color(a) for a in accuracies]
    labels = [FACT_LABELS.get(ft, ft) for ft in fact_types]

    overall = sum(r["correct"] for r in rows) / len(rows)

    fig, ax = plt.subplots(figsize=(9.5, 5), facecolor=COLOR_SURFACE)
    ax.set_facecolor(COLOR_SURFACE)

    bars = ax.bar(labels, [a * 100 for a in accuracies], color=colors, width=0.5, zorder=3)

    for bar, acc, n in zip(bars, accuracies, counts):
        ax.text(
            bar.get_x() + bar.get_width() / 2,
            bar.get_height() + 2,
            f"{acc:.0%} (n={n})",
            ha="center", va="bottom", fontsize=9, color=COLOR_INK,
        )

    ax.set_ylim(0, 110)
    ax.set_ylabel("Trefferquote", color=COLOR_MUTED, fontsize=9)
    ax.set_title(
        f"RAG-Auswertung: {len(rows)} Fragen, {overall:.0%} gesamt richtig",
        color=COLOR_INK, fontsize=12, loc="left", pad=14,
    )

    ax.yaxis.grid(True, color=COLOR_GRID, linewidth=1, zorder=0)
    ax.set_axisbelow(True)
    for spine in ("top", "right", "left"):
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color(COLOR_GRID)
    ax.tick_params(axis="both", colors=COLOR_MUTED, length=0)
    ax.set_yticks([0, 25, 50, 75, 100])
    ax.set_yticklabels(["0%", "25%", "50%", "75%", "100%"])
    plt.setp(ax.get_xticklabels(), rotation=15, ha="right")

    fig.tight_layout()
    fig.savefig(PNG_PATH, dpi=150)
    print(f"chart gespeichert: {PNG_PATH}")


def build_heatmap(rows: list[dict]) -> None:
    """bild x fakt-typ raster, deckt muster auf die der aggregierte balken pro
    fakt-typ verdeckt (z.b. wenn ausgerechnet ein bestimmtes bild durchgehend
    falsch beantwortet wird, obwohl jeder fakt-typ einzeln gut aussieht)"""
    fact_types = sorted({r["fact_type"] for r in rows}, key=lambda ft: FACT_LABELS.get(ft, ft))
    image_ids = sorted({r["image_id"] for r in rows}, key=int)
    by_cell = {(r["image_id"], r["fact_type"]): r["correct"] for r in rows}

    # 0 = nicht gefragt (fehlt in den rohen metadaten), 1 = falsch, 2 = richtig
    grid = []
    for image_id in image_ids:
        row_values = []
        for fact_type in fact_types:
            cell = by_cell.get((image_id, fact_type))
            row_values.append(2 if cell is True else 1 if cell is False else 0)
        grid.append(row_values)

    cmap = plt.matplotlib.colors.ListedColormap([COLOR_GRID, COLOR_CRITICAL, COLOR_GOOD])

    fig_height = max(4, 0.28 * len(image_ids))
    fig, ax = plt.subplots(figsize=(7, fig_height), facecolor=COLOR_SURFACE)
    ax.set_facecolor(COLOR_SURFACE)
    ax.imshow(grid, cmap=cmap, vmin=0, vmax=2, aspect="auto")

    ax.set_xticks(range(len(fact_types)))
    ax.set_xticklabels([FACT_LABELS.get(ft, ft) for ft in fact_types], rotation=30, ha="right")
    ax.set_yticks(range(len(image_ids)))
    ax.set_yticklabels(image_ids, fontsize=7)
    ax.tick_params(axis="both", colors=COLOR_MUTED, length=0)
    for spine in ax.spines.values():
        spine.set_visible(False)

    # 2px surface-luecke zwischen zellen, wie im rest des projekts als gitterlinie
    ax.set_xticks([x - 0.5 for x in range(1, len(fact_types))], minor=True)
    ax.set_yticks([y - 0.5 for y in range(1, len(image_ids))], minor=True)
    ax.grid(which="minor", color=COLOR_SURFACE, linewidth=2)

    legend_items = [
        plt.matplotlib.patches.Patch(facecolor=COLOR_GOOD, label="richtig"),
        plt.matplotlib.patches.Patch(facecolor=COLOR_CRITICAL, label="falsch"),
        plt.matplotlib.patches.Patch(facecolor=COLOR_GRID, label="nicht gefragt (Feld fehlt)"),
    ]
    ax.legend(handles=legend_items, loc="lower center", bbox_to_anchor=(0.5, 1.02),
              ncol=3, frameon=False, labelcolor=COLOR_INK, fontsize=8)

    fig.suptitle("Richtig/falsch pro Bild und Fakt-Typ", color=COLOR_INK, fontsize=12, x=0.02, ha="left", y=1.06)
    fig.tight_layout()
    fig.savefig(HEATMAP_PATH, dpi=150, bbox_inches="tight")
    print(f"heatmap gespeichert: {HEATMAP_PATH}")


def print_wrong_answers(rows: list[dict]) -> None:
    wrong = [r for r in rows if not r["correct"]]
    if not wrong:
        print("\nalle antworten korrekt.")
        return
    print(f"\n{len(wrong)} falsche antworten zum gegenlesen:")
    for r in wrong:
        print(f"  [{r['image_id']}/{r['fact_type']}] erwartet={r['expected']!r}")
        print(f"    antwort: {r['answer'][:150]!r}")


def main() -> None:
    rows = load_rows()
    if not rows:
        print(f"keine daten in {CSV_PATH} - erst python rag_eval.py laufen lassen")
        return
    build_chart(rows)
    build_heatmap(rows)
    print_wrong_answers(rows)


if __name__ == "__main__":
    main()
