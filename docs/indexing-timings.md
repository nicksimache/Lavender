# Measuring indexing

Rebuild the solution and restart Lavender to load the instrumented backend. Open
the same project normally. A timing summary appears in chat after indexing; failed
runs include completed stages and the failing stage's elapsed time in the error.

Each C# run writes a JSON report under:
`%LOCALAPPDATA%\Lavender\Diagnostics\Indexing`

Click **Timings** in the chat header to open this folder. A report is created as
soon as indexing starts, updated after each completed stage, and finalized on
success/failure. Its `Status` is `Running`, `Completed`, or `Failed`.
Lavender builds the MCP server incrementally into `artifacts/mcp-runtime` before
connecting, so rebuilding only the desktop cannot leave old indexing code running.
This build happens during startup and is not part of the indexing timings.
They record scan/chunk, Roslyn load, symbol/compilation, relationships, dependencies,
query-service setup, backend readiness, embedding HTTP round trip, and publication.
They include file/chunk/symbol counts. Diagnostics and Git queries are not performed
as part of service setup. Reports measure indexing, not desktop startup or file-picker time.

The Python breakdown includes embedding API milliseconds, API request count,
database-write milliseconds, other overhead, and total handler time. These nested
times are INCLUDED in the C# embedding HTTP duration: do not add both together.
API time includes SDK retries/network waiting. Request count counts SDK invocations,
not individual HTTP retry attempts. It does not measure API billing or token usage.

Python also saves each run under `backend/app/artifacts/indexing/embed-*.json`,
including failed requests. This lets you inspect backend completion if the desktop
HTTP request times out before receiving the response. No source chunks or vectors
are written into timing reports. HTTP timeouts remain unchanged.

Compare an initial open, an unchanged reopen, and a reopen after a small edit. Record
the project size and any concurrent indexing.

## Batched embeddings and reuse

The backend sends at most 35 distinct uncached texts per embedding request
(`BATCH_SIZE` in `backend/app/main.py`). This uses the normal synchronous embeddings
endpoint with an array of inputs, not the asynchronous OpenAI Batch service.
Response indices associate vectors with inputs even if the response order differs.

Existing chunk IDs identify locations/declarations; they do not consistently include
content. The SQLite cache at `backend/app/lavender_vectors/embedding-cache.sqlite3`
uses SHA-256 of the exact embedding text and embedding model instead. Changed text
or model requires new embeddings; unchanged texts are reused across runs and projects.
Current chunk metadata is always rebuilt, and deleted chunks leave the searchable
snapshot. Each normalized project path has its own LanceDB table. Older vectors from
before this cache was introduced are not imported because their model is not recorded.

Reports now include `batch_size`, `cache_hits`, `embedded_chunks`, and
`embedding_requests`. For 273 distinct uncached chunks, expect 8 requests; reopening
without changes should yield 273 cache hits and 0 chunk embedding requests. Query
embeddings still make their own API requests and are not counted in indexing reports.
Failed runs retain successfully cached batches for retry. The cache currently has
no automatic eviction. Normal per-input API token limits still apply.

## Chat during indexing

After the folder/history opens, chat and source reading are available while indexing
continues. Vector indexing now runs before Roslyn structural indexing. Cached chunks
are published first, followed by each successful batch. Symbol/relationship tools
require the structural index and report that it is still building or wait boundedly.
Switching projects is queued until indexing and the current chat finish.

Semantic search embeds the query once and retries against newly published batches
when the nearest group centroid's cosine distance exceeds `maxDistance` (default 0.65). A sufficiently
close match can return early. Otherwise it waits until completion/failure or up to
45 seconds after query embedding. This threshold is a tunable heuristic, not a
calibrated confidence score. Completed runs return their best results even when weak;
partial, failed and low-confidence results are labeled for the agent. Waiting ends
on failure, and searches cannot read an earlier run's snapshot.

Try asking a code question immediately after opening, reopen unchanged, then edit
one function and reopen. Compare cache hits, requests and elapsed times. There is
still no automatic file-watcher reindex; opening/reindexing refreshes the chunks.

## Semantic groups

`CLUSTER_COUNT = 25` in `backend/app/main.py`. For each published snapshot:

1. Order chunks by their original position in the indexing request, independent of cache hits.
2. Use the first 25 available chunks as seeds (or all chunks for a smaller project).
3. Assign every other chunk to the seed with smallest cosine distance. Seeds own
   themselves; ties for other chunks choose the earlier seed.
4. Compute each group's arithmetic-mean embedding. There is no iterative reassignment.

Search ranks these means using cosine distance. MCP parameter `groupCount` and HTTP
parameter `group_count` select 1 or 2 groups, default 2, replacing the old `topK` /
`top_k` chunk limit. Every member of the selected groups is returned, with `group_id`,
`group_distance` and the individual chunk's `distance`. Group IDs are local to a
snapshot, not permanent symbol IDs. Partial snapshots are rebuilt as batches arrive;
the final snapshot uses the first 25 chunks in the complete request.

Reports include `clustering_ms` and `cluster_count`. Clustering runs locally using
NumPy (already a dependency) and requires no extra embedding requests. Membership
and centroids are rebuilt from cached vectors on reopen. MCP still limits each code
excerpt to 4,000 characters, and the agent's existing overall tool/context budgets
can truncate very large results; there is no chunk-count cap in semantic retrieval.
