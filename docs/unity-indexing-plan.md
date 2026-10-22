# Unity indexing and MCP implementation plan

Status: first implementation available. Current-file Roslyn diagnostics and the live
Unity catalog and scene tools are implemented; see `unity-tools.md` for installation,
supported operations and limits. The broader scope below remains a roadmap.

## Existing foundations and compiler feedback

Lavender already has Roslyn workspaces, semantic classification/completion, and the
`lavender_get_diagnostics` tool backed by FreshDiagnosticsService. EditorAssistance
now renders compiler diagnostics in a Problems panel with file,
line, severity and diagnostic code, plus underlines and hover messages. Analyze the
current unsaved document after a short idle interval through the existing language
workspace; cancel stale analysis and reject results from old document revisions.
Provide explicit project-wide analysis separately. This work is moderate UI/service
integration rather than writing or adding a compiler. No LLM call is needed.

For Unity projects, stream Unity's actual compilation results through the bridge
and label their source separately from Roslyn editor diagnostics. Unity controls
assembly definitions, platform symbols, references and compilation lifecycle; a
Roslyn-only result is not proof of a successful Unity build or Play Mode run.

## Index scope

Start with structured, local data, queried on demand. Do not embed every Transform
or Inspector change. Use the live Unity Editor as the authority for unsaved state.

| Data | First version | Why it matters |
| --- | --- | --- |
| Editor/project identity | Canonical project root, Unity version, session ID, connection generation, compilation/reload and Play Mode state | Route commands to the correct project; detect reconnects |
| Loaded scenes and stage | Scene GUID/path, loaded/active/dirty status; current prefab stage | Know where a new object will be placed; preserve unsaved work |
| GameObjects | Object ID, name, parent/child IDs, sibling order, scene ID, active state, tag, layer, local/world Transform | Resolve targets, hierarchy edits, explicit placement coordinates |
| Components | Component ID, owner, fully qualified type, enabled state, MonoScript GUID/class, missing-script status | Know which scripts and built-in components are attached |
| Inspector values | On-demand serialized property paths, value types, bounded values, arrays and object-reference IDs | Read actual configured values and safely assign properties |
| Asset catalog | GUID, local file ID for sub-assets, path, type, labels, dependencies, revision/hash | Find prefabs, models, materials, sprites, audio and ScriptableObjects without loading everything |
| Prefabs | Asset identity; lazy hierarchy/component summaries; instance/source relationship and overrides | Instantiate the right prefab and distinguish instance changes from asset changes |
| Code-to-scene links | Script GUID → component type → instances; serialized fields and referenced assets | Answer which objects use a script and connect code fixes to scene state |
| Diagnostics | Unity compiler errors/warnings and bounded runtime exceptions with source/stack/context object | Verify edits and diagnose behavior |

Later extensions: renderer/collider bounds for placement checks, terrain/navigation,
animation/controller graphs, UI layout/canvas details, build scenes, input actions,
physics/project settings and visual previews. Initially provide an inventory of
unloaded scenes; deep inspection of them must be an explicit operation that does
not disturb the user's open scenes. No mass loading of assets or arbitrary getter
evaluation. Exclude generated caches, binary mesh/texture payloads and per-frame
simulation values from the index.

## Identity, storage and freshness

- Saved assets: GUID plus local file ID. Saved scene objects: GlobalObjectId where
  resolvable. These IDs have scene/loading limitations; they are not universal
  persistent handles. Names and hierarchy paths are display/search fields only.
- Unsaved/new/runtime objects: session-scoped IDs, explicitly invalidated on reload
  or scene lifetime changes. Re-resolve every ID before a mutation.
- Keep an in-memory live snapshot and a local structured asset catalog. Partition
  every response by project/session, with scene/object revision and completeness.
  Persisted snapshots are hints after restart, not authority to edit live objects.
- Listen for object/hierarchy changes, Undo/Redo, scene open/close/save, prefab-stage
  transitions, asset imports/moves/deletes, compilation and assembly reloads.
  Coalesce changes; invalidate affected records and resnapshot after reconnect or
  event overflow. On-demand reads verify current state rather than trusting events
  to cover every editor extension or direct mutation.
- Query by exact name/type/tag/path first. Return paginated summaries, then expand
  the selected objects/components. Optional cached embeddings of stable asset
  descriptions can be added later if text filtering is insufficient.

## Transport and tool boundary

Extend the editor-only integration with a versioned, authenticated local bridge.
The existing navigation pipe is not yet a Unity command transport. The MCP server
must connect to the selected Unity session independently of the chat UI, and must
not restart or own Unity. Queue Unity API work on its main thread. Include request
IDs, timeouts, bounded payload sizes, session generation and cancellation states.
Reconnect around domain reload; report busy/disconnected rather than old success.

Read tools first:

- `unity_get_editor_state`: connection, project, active scenes/stage, selection, mode.
- `unity_find_objects`: scene-scoped filters and paginated component summaries.
- `unity_inspect_object`: hierarchy, Transform, components and prefab relationship.
- `unity_get_component_properties`: bounded serialized values and reference IDs.
- `unity_find_assets`: exact/type/path/label search with lazy prefab inspection.
- `unity_get_diagnostics`: Unity compilation status and bounded errors/warnings.

Then add focused write tools:

- `unity_create_game_object`: empty/primitive object in an explicit loaded scene.
- `unity_instantiate_prefab`: prefab GUID, scene ID, optional parent and Transform.
- `unity_set_transform` / `unity_set_parent`: explicitly local/world coordinates and
  whether to preserve world position when reparenting.
- `unity_add_component` / `unity_set_component_properties`: resolved component
  types and validated serialized property paths/reference types.
- Later: duplicate/delete objects, change materials, create assets, save scenes,
  run tests and enter/exit Play Mode.

Every mutation checks the session and expected object revision, defaults to Edit
Mode, operates on explicit IDs, integrates with Undo, marks the right scene dirty,
and returns resulting IDs plus read-back state. Saving is a distinct operation.
Batch commands should prevalidate, share one Undo group, and report partial
failure honestly; an Undo group alone does not guarantee transactional rollback.
Deduplicate retried request IDs so a timeout cannot create the same object twice.
Use supported prefab/serialized-property APIs to preserve prefab connections and
overrides. No general-purpose arbitrary C# execution tool in the first version.

## Delivery order and acceptance

1. Local bridge + editor state + loaded-scene/asset inventories. Verify correct
   routing with two Unity projects and reconnect after compilation/reload.
2. Object/component inspection and code-to-scene links. Test duplicate names,
   inactive objects, multiple additive scenes, missing scripts, unsaved objects,
   prefab instances and Undo/Redo freshness.
3. Create object, instantiate prefab and Transform tools. Demonstrate “place three
   instances of this prefab beneath this parent”; verify exactly three instances,
   correct coordinate space, unchanged source prefab and one usable Undo action.
   Retry the same request and verify no duplicates. Reject stale/disconnected IDs.
4. Serialized component edits and Unity compiler feedback, then additional tools.

## References

- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/GlobalObjectId.html
- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SerializedObject.html
- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ObjectChangeEvents.html
- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Undo.RegisterCreatedObjectUndo.html
- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Compilation.CompilationPipeline-assemblyCompilationFinished.html
