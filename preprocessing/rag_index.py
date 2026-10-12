# duenner chromadb wrapper, immer eigene embeddings uebergeben

from __future__ import annotations

from pathlib import Path

import chromadb

from rag_embeddings import embed_texts

DATA_ROOT = Path(__file__).resolve().parent.parent / "data" / "rag_index"
COLLECTION_NAME = "bioimage_metadata"

_client = None


def get_collection():
    global _client
    if _client is None:
        DATA_ROOT.mkdir(parents=True, exist_ok=True)
        _client = chromadb.PersistentClient(path=str(DATA_ROOT))
    return _client.get_or_create_collection(COLLECTION_NAME)


def upsert_chunks(chunks: list[dict]) -> None:
    # chunks aus einem adapter, upsert ueber id
    if not chunks:
        return

    embeddings = embed_texts([chunk["text"] for chunk in chunks])
    get_collection().upsert(
        ids=[chunk["id"] for chunk in chunks],
        embeddings=embeddings,
        documents=[chunk["text"] for chunk in chunks],
        metadatas=[
            {
                "source_db": chunk["source_db"],
                "source_id": chunk["source_id"],
                "field_type": chunk["field_type"],
            }
            for chunk in chunks
        ],
    )


def query(text: str, n_results: int = 5, source_id: str | int | None = None) -> list[dict]:
    # naechste treffer zuerst, source_id filtert auf ein bild
    where = {"source_id": str(source_id)} if source_id is not None else None
    [embedding] = embed_texts([text])

    result = get_collection().query(
        query_embeddings=[embedding],
        n_results=n_results,
        where=where,
        include=["documents", "metadatas", "distances"],
    )

    ids = result["ids"][0]
    docs = result["documents"][0]
    metas = result["metadatas"][0]
    dists = result["distances"][0]
    return [
        {"id": ids[i], "text": docs[i], "metadata": metas[i], "distance": dists[i]}
        for i in range(len(ids))
    ]
