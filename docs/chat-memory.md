# Conversations and tool selection

Open a project to load its most recently updated conversation. The chat picker shows
that project's saved conversations; **New chat** starts a separate one. Selecting a
different project switches history and selected context files. Old history files
without project metadata remain in the projectless chat list; they are not silently
assigned to an arbitrary project.

History includes user/assistant text, tool execution records, selected file paths,
and an older-discussion summary. The storage location remains `Agent:HistoryDirectory`
relative to the application output directory. `PersistHistory: false` keeps history
in memory only for the process lifetime.

The model receives the latest `MaxConversationTurns`, up to 24,000 characters of
recent tool evidence (up to 8,000 per turn), and a summary of older discussion.
Summarization uses additional model requests when turns move out of the recent
window. The full original history stays on disk. These are character bounds, not
an exact total token budget. Summaries are lossy and are not authoritative source code.

Evidence includes observation times. A source-file path/size/modification-time
fingerprint invalidates older tool excerpts after project changes. This does not
automatically rebuild the Roslyn/vector indexes: the model is instructed to reread
files or choose the indexing tool before relying on structural/semantic results.
Changes that preserve both file size and modification time are not detected.

Selected files remain paths; the agent is instructed to read relevant files rather
than assume their contents. **Clear files** removes the selection for the current chat.
The status line shows thinking, summarization, and tool activity. **Stop** cancels the
current request; it cannot roll back an operation that a future editing tool has
already completed.

## Adding future tools

The MCP server already discovers `[McpServerToolType]` classes using
`WithToolsFromAssembly()`. Add a `[McpServerTool]` method with a clear description
and parameter descriptions. The client refreshes the tool list each agent turn,
converts the schemas into model tools, and executes the model's selections.
No keyword router or fixed discovery sequence is required.

Tools execute sequentially, so a future edit cannot race another call in the same
batch. Immediate identical calls are blocked when configured, but a read can be
repeated after an intervening edit. Limits still cap the number of calls/iterations.
Editing tools are not implemented by this change. Each future editing tool should
enforce the selected-project boundary, validate its edits, and return what actually
changed; the agent should refresh affected evidence/indexes afterward.

## Checks

From the repository directory:

```powershell
dotnet run --project tests/ConversationMemory/ConversationMemory.csproj
```

This runs offline checks for project isolation, persisted messages/context/summary,
damaged history handling, nonpersistent memory, bounded tool evidence, and source
change detection. It does not call a model.

Manual acceptance checks:

1. Open project A, ask which method performs indexing, then ask a follow-up without
   restating the method. Check that the answer uses the prior evidence or rereads it.
2. Start a new chat; verify the old messages disappear and can be reopened in the picker.
3. Open project B; verify project A's chats and selected files are absent.
4. Restart Lavender, reopen project A, and verify its latest conversation returns.
5. Exceed `MaxConversationTurns`; observe the summarization status and inspect the
   saved JSON for `Summary` and `SummarizedTurnCount`.
6. Edit a source file between questions; ask about that change and verify the agent
   reads current source or reindexes rather than treating earlier evidence as current.
7. Start a response and click Stop. Verify the UI is usable and the cancellation is saved.

The UI builds successfully, but live model choice and summary quality require these
interactive checks with an API key.
