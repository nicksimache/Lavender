# Unity tools and editor diagnostics

## Installation

Copy the **contents** of `integrations/unity/Bridge/` into your Unity project's
`Assets/Editor/LavenderBridge/`. Keep the `.asmdef` alongside the three C# files.
This is separate from `LavenderOpenAsset.cs`, which handles opening scripts in
Lavender from the Unity Console. The bridge starts automatically after compilation.
Restart Lavender after building so its MCP server exposes the new tools.

Open the same Unity project root in Lavender and Unity, running under the same
Windows account and desktop session. Unity must successfully load the bridge once;
fix existing compilation errors if installation cannot finish. No API key or network
port is used for the bridge. Communication uses a project-specific local named pipe
restricted to the current user. Windows is the supported platform for this release.

Try: “Inspect the current scene, create a cube named LavenderTest at (0, 1, 0),
and give it a BoxCollider if it does not already have one.”

## Available tools

| Tool | Purpose |
| --- | --- |
| `unity_get_editor_state` | Loaded scenes, selection, active scene, mode, session and revision |
| `unity_find_objects` | Search loaded scene hierarchy, including inactive objects, by name/type/scene |
| `unity_inspect_object` | Transforms, components, script GUIDs, hierarchy and prefab origin |
| `unity_get_component_properties` | Paginated Inspector properties and editable flags |
| `unity_find_assets` | Unity AssetDatabase search, GUIDs, paths and types |
| `unity_inspect_asset` | Labels, direct dependencies and reference IDs for main/subassets (up to 200 each) |
| `unity_get_diagnostics` | Captured compiler messages and runtime errors; compilation status labels stale snapshots |
| `unity_create_game_object` | Empty object or primitive in an explicit loaded scene |
| `unity_instantiate_prefab` | Linked prefab/model instance |
| `unity_set_transform` | Local or world position/Euler rotation, local scale |
| `unity_set_parent` | Reparent within a scene, preserving world transform |
| `unity_add_component` | Add a concrete component by fully qualified type name |
| `unity_set_component_properties` | Edit supported serialized component properties in a single Undo group |

This is a live structured catalog, queried on demand, separate from source embeddings.
It includes unsaved scenes and updates with Unity's state. It does not create a
persistent semantic index of every Unity asset or parse unloaded scene/prefab contents.

## Editing contract

Read state and inspect targets first. Each write needs the returned session, latest
revision and a unique request ID. Stale revisions are rejected. A retry after a
timeout must use the **identical arguments and request ID** to avoid duplicates.
Successful receipts are retained for the current domain session (maximum 4096).
After domain reload, read state and inspect the result before repeating an edit.
Scene IDs and object/component IDs are session-scoped; never reuse historical IDs.

Writes require idle Edit Mode and are disabled during compilation, import, Play Mode
and Prefab Mode. They mark scenes dirty and support Unity Undo. They do **not** save
scenes automatically. Review the result in Unity and save normally.

Property edits support booleans, integers, finite numbers, strings, enum names,
object references and Vector3 values. Use the exact property paths returned by
inspection. Vectors use `x,y,z` strings for serialized properties; transform tools
take three-element numeric arrays. Object references use returned scene/component IDs
or `asset:GUID:localFileId` IDs from asset inspection; an empty string clears one.
Protected structural properties are blocked. Transform properties use the dedicated
transform tool. Arbitrary methods, collection resizing, object deletion, asset edits,
scene loading/saving, and prefab asset editing are not exposed in this release.

The diagnostics feed captures messages after bridge initialization, with a bounded
200-message history. Previously captured compilation results survive domain reload
with a `last_compilation_` status. It is not a complete history of Unity's Console.

## C# editor errors

The current C# file is analyzed with Roslyn after 350 ms idle, using unsaved buffers.
Errors and warnings appear in the Problems panel, underlines and hover messages.
Double-click a problem to navigate to its location. Stale requests are canceled when
the file or text changes. Project-wide diagnostics remain available through
`lavender_get_diagnostics`; Unity compilation results use `unity_get_diagnostics`.
Roslyn diagnostics alone do not establish that a Unity build or Play Mode succeeds.

## Validation

`tests/EditorDiagnostics` checks syntax/semantic errors, corrected buffers, file
isolation and cancellation. `tests/UnityNavigationCompile` compiles the integration
against installed Unity reference assemblies. `tests/UnityBridge/BridgeSmokeTests.cs`
can run in a disposable Unity project using `-batchmode -nographics -executeMethod
BridgeSmokeTests.Run`; it exits explicitly and writes `smoke-results.txt` in that
project. Never run this fixture inside a real project: it replaces the loaded scene
and creates `Assets/Fixture.prefab`.
