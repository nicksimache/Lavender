# Hierarchical semantic search

The backend builds a two-level spherical K-means hierarchy from normalized chunk embeddings.
Farthest-point initialization is deterministic in chunk order; cosine assignment and
normalized centroid updates repeat until assignments stabilize or 30 iterations finish.
Empty clusters are omitted. Identical directions remain together rather than creating
artificial singleton groups.

Each partition uses up to 25 centers, with its branching factor based on a target of
32 chunks per leaf. Groups larger than that target split once more, unless no useful
split is possible. The target is not a hard size limit: depth, branching limits and
duplicate vectors can leave larger leaves. Small projects may have only one level.

Search explores the two closest top-level centroids, then chooses the nearest one or
two leaf centroids among those branches. `group_count` controls selected leaves, not
the number of chunks. All chunks in those leaves are returned, ranked globally by
their cosine distance to the query. Integer group IDs identify nodes within a
snapshot; they are not stable across reindexing.

This is approximate retrieval: pruning other branches can miss a globally nearest
chunk. Multiple-branch exploration reduces that risk but does not eliminate it.
Real-project relevance and latency still need measurement before claiming an
improvement over exhaustive cosine search.

The hierarchy is rebuilt for published partial indexing snapshots and for legacy
on-disk rows. Existing embeddings remain usable; no embedding-model change is needed.
The existing centroid confidence threshold controls waiting during indexing, not a
hard filter on completed search results. Restart the backend to load this change.
