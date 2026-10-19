# Code editor

Opening or switching files prepares highlighting before activating the target tab.
The current document stays visible and editable; new tabs are added only when ready.
New navigation cancels an older pending open, and changes to open buffers invalidate
pending preparation. Unsupported files and classification failures use basic highlighting.

Lavender uses AvalonEdit for editing, built-in language highlighting, and the completion popup.
Roslyn supplies C# member suggestions and semantic classifications. The former custom
`SyntaxHighlighter` and `RichTextBoxRenderer` are removed.

## Suggestions

- Open a C# file belonging to the selected solution/project.
- Type `.` after an object or type, or press **Ctrl+Space** to request suggestions.
- Continue typing to filter an open popup. Use arrow keys to choose an item,
  **Tab/Enter** to insert it, or **Escape** to dismiss it.
- Suggestions use all currently open buffers, including unsaved changes. Inserting
  a suggestion edits the document; normal Save is still required to write to disk.
- The first request loads a separate editor Roslyn workspace in the background.
  Dependency restoration and valid project references are needed for complete results.
  If you type or move the caret while a request is pending, obsolete results are discarded;
  Ctrl+Space requests fresh results.
- Files outside the selected project and non-C# files retain AvalonEdit highlighting,
  but do not have Roslyn completion. Failures are reported in the activity log.
- This is identifier/member completion, not a complete IDE: multi-edit snippets,
  import insertion, parameter help, and automatic project-file watching are not implemented.
  Reopen the project after external project/reference changes. Successful agent file edits
  invalidate the editor workspace automatically.

## Lavender Night palette

| Category | Color |
| --- | --- |
| Keywords | Lavender `#C4A7E7` |
| Types | Teal `#7BDAC6` |
| Methods | Gold `#EBCB8B` |
| Variables, parameters, fields | Sky blue `#9CDCFE` |
| Properties and events | Periwinkle `#B8C0FF` |
| Strings | Peach `#E9B38F` |
| Numbers and constants | Pale green `#B5CEA8` |
| Comments | Sage `#8FAF87` |
| Operators | Blue `#89B4FA` |

AvalonEdit provides immediate highlighting. After typing pauses, Roslyn overlays
semantic colors for C#. Edits discard colors only for intersecting tokens and shift
the remaining spans, retaining their colors until fresh classifications arrive.
Project loading survives typing cancellation, and successive requests reuse the
latest Roslyn solution with incremental text changes instead of rebuilding every buffer.
The first semantic pass still waits for the MSBuild workspace to load.
Project selection starts the editor workspace loading in the background, even before
a C# file is selected. Completion and highlighting share that loaded workspace.
Startup warmup also prepares C# project compilations in dependency order and retains
them for editor reuse. Opening
a file before preparation finishes can still wait for it. Project reload releases
the retained compilations; editor analysis retains the latest compilation it obtains.
File selection and project refresh start analysis immediately. Typing is batched in
50 ms windows with one active analysis and one pending latest-state request, rather
than creating a task for every key. Results for older text revisions are discarded.
Semantic analysis runs on a background task while
AvalonEdit renders on the UI thread. Line rendering uses a binary search over indexed
span ends rather than scanning from the beginning of the document for each line.
Edits update the affected suffix of the span index in place. Semantic results redraw
only changed line ranges, merging adjacent lines; changes to multiline constructs
can therefore redraw beyond the edited line. Whole-file classification is retained.
The first two classification attempts per editor session, and files of at least
20,000 characters, use up to three concurrent range requests over the same document.
Boundaries preserve tokens and their trivia. Results are merged, exact duplicates
removed, and sorted before a single UI application; other requests use one range.
Other languages use their bundled AvalonEdit definitions with the same palette.

## Code map

- `EditorTheme`: centralized palette, built-in definition styling, semantic color overlay.
- `EditorAssistance`: editor events, popup lifecycle, debouncing, and stale-result checks.
- `EditorLanguageService`: separate MSBuild workspace and immutable unsaved-buffer snapshots.
- `CSharpSuggestions`: Roslyn completion items converted to single-span editor suggestions.
- `MainWindow`: wires the helper to the selected project and open tabs.

Run the integration checks from the repository root:

```powershell
dotnet run --project tests/EditorAssistance/EditorAssistance.csproj -- tests/EditorAssistance/EditorAssistance.csproj
```
