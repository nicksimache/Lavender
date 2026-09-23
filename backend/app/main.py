import hashlib
import json
import time
import uuid
import os
import sqlite3
import threading
from pathlib import Path

import lancedb
import numpy as np
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
from openai import OpenAI

app = FastAPI()
client = OpenAI()

db = lancedb.connect("./lavender_vectors")
TABLE_NAME = "code_chunks"
EMBED_MODEL = "text-embedding-3-small"
SOURCE_REVISION = hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper()
BATCH_SIZE = 35
CLUSTER_COUNT = 25
CACHE_PATH = Path("./lavender_vectors/embedding-cache.sqlite3")
REPORT_DIRECTORY = Path(__file__).resolve().parent / "artifacts" / "indexing"
changed = threading.Condition()
index_lock = threading.Lock()
sessions = {}


class CodeChunk(BaseModel):
    id: str
    file_path: str
    chunk_type: str
    namespace: str = ""
    class_name: str = ""
    member_name: str = ""
    signature: str = ""
    start_line: int = 0
    end_line: int = 0
    code: str
    embedding_text: str


class EmbedProjectRequest(BaseModel):
    chunks: list[CodeChunk]
    project_path: str = ""
    index_id: str = "legacy"


class SearchRequest(BaseModel):
    query: str
    group_count: int = Field(default=2, ge=1, le=2)
    project_path: str = ""
    index_id: str = "legacy"
    max_distance: float = Field(default=0.65, ge=0, le=2)
    wait_seconds: float = Field(default=45, ge=0, le=60)


def table_name(project_path):
    if not project_path:
        return TABLE_NAME
    normalized = os.path.normcase(os.path.abspath(project_path)).rstrip("/\\")
    return "chunks_" + hashlib.sha256(normalized.encode()).hexdigest()


def cache_key(text):
    return hashlib.sha256((EMBED_MODEL + "\0" + text).encode()).hexdigest()


def unit_vectors(vectors):
    values = np.asarray(vectors, dtype=np.float64)
    if not np.isfinite(values).all():
        raise ValueError("Embedding contains non-finite values")
    norms = np.linalg.norm(values, axis=-1, keepdims=True)
    return np.divide(values, norms, out=np.zeros_like(values), where=norms > 0)


def build_groups(rows):
    """One assignment pass against the first K seeds, then arithmetic-mean centroids."""
    ordered = sorted(rows, key=lambda row: row.get("chunk_order", 0))
    if not ordered:
        return []
    vectors = np.asarray([row["vector"] for row in ordered], dtype=np.float64)
    normalized = unit_vectors(vectors)
    k = min(CLUSTER_COUNT, len(ordered))
    assignments = np.argmax(normalized @ normalized[:k].T, axis=1)
    # A seed owns itself when identical seeds tie, keeping all K groups nonempty.
    assignments[:k] = np.arange(k)
    return [dict(id=i, centroid=vectors[assignments == i].mean(axis=0),
                 rows=[ordered[j] for j in np.flatnonzero(assignments == i)])
            for i in range(k)]


def search_groups(groups, query_vector, count):
    if not groups:
        return [], []
    query = unit_vectors(query_vector)
    distances = np.clip(1 - unit_vectors([g["centroid"] for g in groups]) @ query, 0, 2)
    selected = np.argsort(distances, kind="stable")[:count]
    results, summaries = [], []
    for index in selected:
        group = groups[index]
        distance = float(distances[index])
        summaries.append(dict(group_id=group["id"], distance=distance, chunk_count=len(group["rows"])))
        chunk_distances = np.clip(1 - unit_vectors([r["vector"] for r in group["rows"]]) @ query, 0, 2)
        for row, chunk_distance in zip(group["rows"], chunk_distances):
            results.append(dict(row, _distance=float(chunk_distance), group_id=group["id"], group_distance=distance))
    # Rank within the selected groups, without dropping any members.
    results.sort(key=lambda row: (row["group_distance"], row["_distance"]))
    return results, summaries


def get_embeddings(texts):
    # Use the response index, not its order, to associate each vector with its chunk.
    response = client.embeddings.create(model=EMBED_MODEL, input=texts)
    ordered = sorted(response.data, key=lambda item: item.index)
    if [item.index for item in ordered] != list(range(len(texts))):
        raise ValueError("Embedding response has missing or duplicate indices")
    return [item.embedding for item in ordered]


def get_embedding(text: str) -> list[float]:
    response = client.embeddings.create(
        model=EMBED_MODEL,
        input=text
    )
    return response.data[0].embedding

@app.get("/")
def root():
    return {
        "message": "backend server running",
        "service": "lavender",
        "source_revision": SOURCE_REVISION,
    }

@app.post("/embed-project")
def embed_project(request: EmbedProjectRequest):
    if not index_lock.acquire(blocking=False):
        raise HTTPException(status_code=409, detail="An indexing run is already active")
    started = time.perf_counter()
    timings = {"embedding_api_ms": 0.0, "table_write_ms": 0.0,
               "embedding_requests": 0, "chunk_count": len(request.chunks),
               "cache_hits": 0, "embedded_chunks": 0, "batch_size": BATCH_SIZE,
               "clustering_ms": 0.0, "cluster_count": 0}
    rows = []
    name = table_name(request.project_path)
    state = {"status": "indexing", "indexed_chunks": 0, "total_chunks": len(request.chunks), "error": None, "groups": []}
    connection = None
    with changed:
        for previous in sessions.values():
            if previous.get("table") == name:
                previous.update(status="superseded", indexed_chunks=0, groups=[], error="A newer indexing run replaced this snapshot")
        state["table"] = name
        sessions[request.index_id] = state
        changed.notify_all()
    try:
        CACHE_PATH.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(CACHE_PATH)
        connection.execute("CREATE TABLE IF NOT EXISTS embeddings (key TEXT PRIMARY KEY, vector TEXT NOT NULL)")
        pending = {}
        for position, chunk in enumerate(request.chunks):
            chunk_row = dict(chunk.model_dump(), chunk_order=position)
            key = cache_key(chunk.embedding_text)
            cached = connection.execute("SELECT vector FROM embeddings WHERE key = ?", (key,)).fetchone()
            if cached:
                rows.append(dict(chunk_row, vector=json.loads(cached[0])))
                timings["cache_hits"] += 1
            else:
                pending.setdefault(key, []).append(chunk_row)

        # Replace the searchable snapshot with only current chunks; removed chunks disappear.
        write_started = time.perf_counter()
        clustering_before = timings["clustering_ms"]
        with changed:
            if name in db.table_names():
                db.drop_table(name)
            if rows:
                db.create_table(name, rows)
            state["indexed_chunks"] = len(rows)
            cluster_started = time.perf_counter()
            state["groups"] = build_groups(rows)
            timings["clustering_ms"] += (time.perf_counter() - cluster_started) * 1000
            timings["cluster_count"] = len(state["groups"])
            changed.notify_all()
        timings["table_write_ms"] += (time.perf_counter() - write_started) * 1000 - (timings["clustering_ms"] - clustering_before)
        keys = list(pending)
        for offset in range(0, len(keys), BATCH_SIZE):
            batch = keys[offset:offset + BATCH_SIZE]
            api_started = time.perf_counter()
            timings["embedding_requests"] += 1
            try:
                vectors = get_embeddings([pending[key][0]["embedding_text"] for key in batch])
            finally:
                timings["embedding_api_ms"] += (time.perf_counter() - api_started) * 1000
            batch_rows = []
            for key, vector in zip(batch, vectors):
                connection.execute("INSERT OR REPLACE INTO embeddings VALUES (?, ?)", (key, json.dumps(vector)))
                batch_rows.extend(dict(chunk, vector=vector) for chunk in pending[key])
            connection.commit()
            timings["embedded_chunks"] += len(batch_rows)
            write_started = time.perf_counter()
            clustering_before = timings["clustering_ms"]
            with changed:
                if state["indexed_chunks"]:
                    db.open_table(name).add(batch_rows)
                else:
                    db.create_table(name, batch_rows)
                rows.extend(batch_rows)
                state["indexed_chunks"] = len(rows)
                cluster_started = time.perf_counter()
                state["groups"] = build_groups(rows)
                timings["clustering_ms"] += (time.perf_counter() - cluster_started) * 1000
                timings["cluster_count"] = len(state["groups"])
                changed.notify_all()
            timings["table_write_ms"] += (time.perf_counter() - write_started) * 1000 - (timings["clustering_ms"] - clustering_before)
        with changed:
            state["status"] = "completed"
            changed.notify_all()
    except Exception as exc:
        timings["error"] = str(exc)
        with changed:
            state.update(status="failed", error=str(exc))
            changed.notify_all()
        raise
    finally:
        if connection is not None:
            connection.close()
        index_lock.release()
        timings["total_ms"] = (time.perf_counter() - started) * 1000
        timings["other_ms"] = max(0, timings["total_ms"] - timings["embedding_api_ms"] - timings["table_write_ms"] - timings["clustering_ms"])
        # Keep timing data even if the desktop times out waiting for this request.
        report_dir = REPORT_DIRECTORY
        try:
            report_dir.mkdir(parents=True, exist_ok=True)
            report = report_dir / f"embed-{uuid.uuid4().hex}.json"
            timings["report_path"] = str(report)
            report.write_text(json.dumps(timings, indent=2), encoding="utf-8")
        except OSError as exc:
            timings["logging_error"] = str(exc)
    return {
        "message": "Project embedded",
        "chunk_count": len(rows),
        "timings": timings,
    }


@app.post("/search")
def search(request: SearchRequest):
    stage = "opening the vector index"
    try:
        name = table_name(request.project_path)
        # Legacy callers retain the explicit missing-table diagnostic.
        if request.index_id == "legacy" and request.index_id not in sessions:
            db.open_table(name)
        stage = "creating the query embedding"
        query_vector = get_embedding(request.query)

        stage = "searching the vector index"
        deadline = time.monotonic() + request.wait_seconds
        with changed:
            while True:
                state = sessions.get(request.index_id)
                raw_results = []
                selected_groups = []
                if state and state.get("table") != name:
                    raise ValueError("Index run does not belong to the requested project")
                if (state is None and request.index_id == "legacy") or (state and state["indexed_chunks"]):
                    groups = state["groups"] if state else build_groups(db.open_table(name).to_arrow().to_pylist())
                    raw_results, selected_groups = search_groups(groups, query_vector, request.group_count)
                complete = state is None and request.index_id == "legacy" or state and state["status"] != "indexing"
                close_enough = bool(selected_groups) and selected_groups[0]["distance"] <= request.max_distance
                if complete or close_enough or time.monotonic() >= deadline:
                    break
                changed.wait(timeout=max(0, deadline - time.monotonic()))
            snapshot = dict(state) if state else {"status": "completed" if request.index_id == "legacy" else "pending", "indexed_chunks": len(raw_results)}

        # This backend runs with redirected output under the desktop app.
        # Writing debug results to a closed pipe must not break a valid search.
        stage = "formatting search results"
        results = []

        for row in raw_results:
            results.append({
                "file_path": row["file_path"],
                "chunk_type": row["chunk_type"],
                "namespace": row["namespace"],
                "class_name": row["class_name"],
                "member_name": row["member_name"],
                "signature": row["signature"],
                "start_line": row["start_line"],
                "end_line": row["end_line"],
                "code": row["code"],
                "distance": row["_distance"],
                "group_id": row["group_id"], "group_distance": row["group_distance"]
            })

        return {
            "results": results,
            "groups": selected_groups,
            "indexing_status": snapshot["status"],
            "indexed_chunks": snapshot["indexed_chunks"],
            "is_partial": snapshot["status"] != "completed",
            "low_confidence": not close_enough,
            "indexing_error": snapshot.get("error"),
        }
    except Exception as exc:
        raise HTTPException(
            status_code=500,
            detail=f"Search failed while {stage}: {exc}",
        ) from exc
