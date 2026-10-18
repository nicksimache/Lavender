# Code editor

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
semantic colors for C#; outdated classifications are cleared immediately on edits.
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
