# API usage tracking

Tracking is enabled automatically. Restart Lavender after building.
Logs are local JSONL files in `%LOCALAPPDATA%\Lavender\ApiUsage`, split by
process and UTC date. Set `LAVENDER_API_LOG_DIRECTORY` before launching to
override this for both desktop and Python. Logs contain full prompts, source
code, tool definitions, arguments, and tool results sent in request bodies.
Authorization headers are excluded. Logs persist until you delete them.

Run from the repository:

```powershell
.venv\Scripts\python.exe tools\api_usage_summary.py
```

The summary groups HTTP attempts, errors, missing usage, and reported input,
output, total, cached, and reasoning tokens by model and trigger. Add
`--directory PATH` to summarize another log folder. Cached and reasoning
tokens are subsets, not additions to total tokens. No pricing assumptions
are made.

Each outgoing provider HTTP attempt has a `request` event with its exact
serialized `request_body` and a matching `response` or `error` event keyed
by `request_id`. SDK retries are separate attempts. Responses include HTTP
status, elapsed time, provider request ID when supplied, and raw usage.
Chat streaming records a separate `stream_usage` event, joined by
`logical_call_id`; HTTP duration is time to headers, while stream duration
includes consumption. Missing usage (including interrupted streams and
failed requests) is unknown, never an assertion of zero billable tokens.
An abrupt process exit can leave a request without a completion event.
Disk-write failures are reported through diagnostic logging without failing
the API request.

Desktop context identifies conversation/turn, original user text, agent
iteration, and preceding tool names/arguments. Special triggers identify
history summaries and final-answer requests. The full request includes
tool results and their call IDs. Backend context identifies the indexing
tool or semantic-search tool, project/index ID, batch number and source
chunk mappings, or the exact search query. Backend and desktop events are
not yet joined by a shared cross-process tool-call ID.

Startup restores local state, starts Python, probes `GET localhost:8000/`,
connects MCP over stdio, and lists its tools. With a restored/opened project,
`lavender_index_project` scans/chunks files, checks backend health, and posts
to `/embed-project`. Python deduplicates embedding text, looks up its SQLite
cache by model and text, and calls OpenAI embeddings for cache misses in
batches of 35 using `text-embedding-3-small`. It then builds local vector
groups; C# loads Roslyn and builds the symbol index. All-cache-hit indexing
makes zero embedding calls. Semantic search posts to `/search` and embeds
each query using the same embedding model.

The configured default chat model is `gpt-6-luna` with reasoning `medium`.
Startup/indexing does not invoke chat. User turns invoke Responses for
`gpt-6*` tool loops, otherwise Chat Completions. History summaries and Chat
Completions final/streaming answers also consume tokens. Normal completion
now displays the model's existing answer without a second generation call.
A final-answer request remains when an iteration/tool limit interrupts work.

Outgoing tool context retains one copy of structured MCP results instead of
also repeating their text representation. The agent uses the hash-bearing
`lavender_read_file` tool, which accepts optional inclusive `startLine` and
`endLine` parameters; the compatibility whole-file reader is no longer
advertised to the agent. Older successful reads of the same path and range
are replaced with notices when their accumulated content reaches 32,000
characters. Call IDs and tool/result pairs remain intact. This reduction
is local and makes no summarization API request. It does invalidate the
cached prefix after the earliest replaced result, so compaction is batched.
`context_compacted` audit events record how many snapshots were removed.
Persisted tool records keep their existing audit content and truncation rules.
This is not a hard cap on total context: unrelated reads, edits, and other
tool results can still grow the conversation. Reasoning remains medium.

Local health checks and MCP transport are not provider calls and are not
included in token totals. Embedding vectors and model response prose are
not duplicated in logs; exact outgoing content and provider usage are.

## Image assets

`lavender_list_assets` scans supported image paths locally through a separate
asset catalog, with filtering and pagination. It does not create embeddings or
make an API request. The catalog is available as soon as the selected project's
path is set, even while code indexing runs, and refreshes from disk when listed.

`lavender_inspect_image` returns dimensions, path and a PNG preview capped at
1024 pixels on the longest edge (first frame for multi-frame images). The agent
attaches that preview as actual image input on its next Responses or Chat
Completions request. Image inspection therefore contributes to model input
usage, attributed to the preceding inspection tool in request context.
The API audit request body includes the base64 image payload. Conversation
tool records retain metadata and an attachment notice instead of base64 text.
Reopened conversations must inspect again to recover the image itself.
