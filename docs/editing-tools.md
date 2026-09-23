# File tools and action summaries

`Lavender.McpServer/Tools/Editing` contains registered tools for creating, reading,
deleting, moving/renaming, and replacing a line range. Methods
declare inputs and return `FileToolResult`, including `Success`, `Changed`, `Status`,
`Message`, paths/ranges, content hashes, and backup paths. The older
`lavender_read_source_file` remains available for inspection, but use
`lavender_read_file` to obtain the hash required for mutations.

`lavender_create_file(path, content)` accepts a relative path and complete text,
including empty text. It uses the selected project's root, creates missing parent
folders, writes UTF-8 without a BOM, and preserves supplied line endings. It rejects
absolute/traversal paths, `.git` components, Windows device names, alternate streams,
and symlink/junction components. Existing files and directories are never overwritten.
A temporary sibling file is written first, then moved with overwrite disabled; failed
or cancelled writes clean up their temporary file. Newly created empty parent folders
may remain after a failed operation.

Mutations share a gate with indexing and fail with a retry message if another operation
holds it. Success marks semantic/structural indexes stale; source reading and Git tools
remain usable, and indexing refreshes the cache. The final tool-use summary records the
confirmed operations, and the explorer/preview refresh after a successful chat response. Rebuild
and restart Lavender to discover the newly registered tool.

## Read, edit, move and delete

- `lavender_read_file(path)` returns full text, `line_count`, encoding and a SHA-256
  `content_hash` of the original bytes. The text limit is 4 MiB; large responses can
  still exceed the agent's separate context budget. UTF-8 and BOM-marked UTF-16 are
  supported. Binary, invalid text and UTF-32 are rejected for reading/line editing.
- `lavender_write_lines(path, startLine, endLine, replacementCode, expectedContentHash)`
  replaces inclusive one-based lines. Empty replacement deletes those lines. Set
  `endLine = startLine - 1` to insert before a line; use `line_count + 1` to append,
  or `1, 0` for an empty file. A terminating newline does not add a phantom final line.
  The existing encoding/BOM is preserved. Inserted text uses the first newline style
  in the file (LF if none). Unchanged text returns `changed: false`.
- `lavender_move_file(path, destinationPath, expectedContentHash)` moves/renames a
  file inside the project, creating missing parent folders. No destination overwrite,
  directory moves or case-only renames are supported.
- `lavender_delete_file(path, expectedContentHash)` removes one file, never a directory.

Read first and pass the exact hash to each mutation. Successful writes return the new
hash. A stale hash stops the operation and requires a fresh read. Hashes are checked
again immediately before committing, and Lavender serializes its own mutations;
this is not an operating-system transaction against simultaneous edits by other apps.
Path traversal, protected `.git` paths, device names, alternate streams and reparse
points are rejected. These safeguards do not implement a hostile-filesystem sandbox.

Before write/move/delete, original bytes and operation metadata are saved under
`%LOCALAPPDATA%/Lavender/Backups/<project-hash>/<operation-id>/`. `backup_path` points
to `original.bin`; adjacent `operation.json` identifies the original path. Restore
manually by copying the saved bytes back. There is no restore tool or automatic backup
eviction yet. Writes publish through a temporary sibling and atomic replacement;
cancelled/failed writes clean up their temporary file. Backups or newly created empty
parent directories can remain after failed operations.

To test through chat, ask Lavender to create a small file under `ToolTest/`, read it,
insert a function, rename it, and finally delete it. Review the reported action list
and backup paths. These tools change files; they do not run builds or tests themselves.

The agent already persists each call under `Turns[].Steps[].ToolCalls[]`. Calls now
also store `Outcome` and `OutcomeMessage`, extracted before large results are clipped.
Outcomes distinguish succeeded, returned data without explicit success, failed,
timed out, cancelled, blocked, and not executed. Both MCP structured content and
JSON inside text content are recognized, including PascalCase fields. Timeouts do
not prove that a future mutating operation was rolled back.

On normal completion or a tool/iteration limit, the agent builds a temporary ordered
`ToolUseSummary[]` from the current turn only. It supplies tool names, compact arguments
(including paths/ranges), outcomes and messages to a final model call with tools
disabled. Large code arguments are represented by character counts; full arguments
remain in the saved records. The prompt distinguishes inspection from modifications
and tells the model not to claim unsuccessful or unimplemented edits. This adds one
model request after normal tool-using runs; no-tool responses remain unchanged.

Git status/diff now use the selected project directory directly rather than waiting
for Roslyn/embedding indexing. Git commands disable the pager and interactive prompts;
diff also disables external helpers and text conversion. Each command has a 10-second
deadline and terminates its process tree on timeout/cancellation. Diff paths are literal
and must stay within the selected project. Successful diff replies report elapsed time.
Untracked files still do not appear in a Git diff: inspect them via status and read-file.
