# Unity source navigation (Windows)

1. Build Lavender and close any older running version before starting the new build.
2. Copy `integrations/unity/LavenderOpenAsset.cs` into your Unity project's
   `Assets/Editor/` folder (create the folder if needed).
3. In Unity, choose **Tools > Lavender > Choose Lavender executable...** and select
   the newly built `Lavender.exe`, normally `bin/Debug/net8.0-windows/Lavender.exe`.
   The isolated validation build is at `artifacts/unity-navigation/Lavender.exe`.
4. Double-click a C# script or a Console error/stack-frame with a source location.
   Lavender opens the file at the supplied one-based line and column.

This is a drop-in editor script registering an `IExternalCodeEditor` integration,
with an early `OnOpenAsset` callback as well. Choosing the executable also selects
Lavender in Unity's External Tools preferences, so Console navigation that bypasses
asset callbacks reaches Lavender. After updating an older script, choose the
executable once again to select Lavender as the external editor.
Disable it with **Tools > Lavender > Enable source navigation** to return to your
previous editor. Preferences are stored per user across Unity projects. The integration
delegates project-file synchronization to the previously selected editor integration
when available (for example, the installed Visual Studio integration).

The project should already have a generated `.sln` or unambiguous `.csproj`, as
required by Lavender's existing project loader. Lavender has no standalone Unity
project generator; keep the previous integration installed for synchronization.
When multiple solutions exist, open the intended project in
Lavender first. A missing solution on cold launch still allows file navigation,
but does not provide a project index. Compilation events and live Console streaming
are not part of this integration.

Lavender accepts:

```powershell
& 'C:\path\Lavender.exe' --project 'C:\MyGame' --file 'C:\MyGame\Assets\Player.cs' --line 42 --column 8
```

`--project` is optional; relative file paths resolve against it when supplied.
Line/column default to 1. Invalid arguments or missing files produce a visible error.
Positions past the end of a document are clamped. Already-open unsaved buffers are
preserved. Clean buffers reload disk changes before navigation.

One Lavender process owns a per-user, per-Windows-session named pipe. Subsequent
launches forward their requests and exit after acknowledgement. No network service
or model request is needed for the navigation itself. Opening a different project
still uses the normal project indexing workflow, which can make embedding requests.
Cross-project requests wait while indexing/chat is busy; the latest pending click
wins. Restart older Lavender processes first because they do not have this listener.

Manual Unity acceptance checks:
- With Lavender closed, double-click a script: one window opens the correct project/file.
- Double-click a Console stack frame: the existing window selects its line/column.
- Repeat on the active file and another file; verify there is still one Lavender window.
- Edit a buffer without saving, then navigate to it: edits remain intact.
- Minimize Lavender, then open a source location: it restores the window.
- Disable the integration and confirm Unity falls back to its configured editor.

Unity callback reference: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Callbacks.OnOpenAssetAttribute.html

The explorer watches external file additions, removals, renames, and saves. Refreshes
are debounced by 400 ms and preserve expanded folders and selection. Unity Library,
Temp, metadata, and other generated files are filtered. Refocusing Lavender also
refreshes the explorer. Clean open buffers reload; unsaved buffers are preserved.
This does not trigger embeddings or automatically rebuild the semantic search index.

Developer checks (close Lavender before the channel test):

```powershell
dotnet run --project tests/ExternalNavigation/ExternalNavigation.csproj
dotnet build tests/UnityNavigationCompile/UnityNavigationCompile.csproj '-p:UnityManagedDirectory=C:/Program Files/Unity/Hub/Editor/6000.0.31f1/Editor/Data/Managed'
```

The first check validates arguments and forwarding between real processes. The
second compiles the drop-in script against installed Unity reference assemblies;
it does not simulate the Unity Console or validate editor UI behavior.
