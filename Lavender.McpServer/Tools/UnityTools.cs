using System.ComponentModel;
using System.Text.Json;
using Lavender.Infrastructure.Unity;
using Lavender.UnityBridge;
using ModelContextProtocol.Server;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class UnityTools(LavenderMcpState state)
{
    private readonly UnityBridgeClient _client = new();
    private Task<JsonElement> Send(BridgeRequest request, CancellationToken token) => _client.ExecuteAsync(
        state.ProjectPath ?? throw new InvalidOperationException("Open the Unity project in Lavender first."), request, token);
    private static BridgeRequest Write(string action, string session, long expectedRevision, string requestId) => new()
        { action = action, session = session, expectedRevision = expectedRevision, requestId = requestId };

    [McpServerTool(Name = "unity_get_editor_state"), Description("Read the selected project's live Unity session, revision, loaded scene IDs, selection, compilation and Play Mode status. Start here. No model or embedding calls. IDs are session-scoped; inspect again after reconnect.")]
    public Task<JsonElement> GetState(CancellationToken cancellationToken = default) => Send(new() { action = "get_state" }, cancellationToken);

    [McpServerTool(Name = "unity_find_objects"), Description("Query the live loaded-scene index including inactive objects. Optional name substring, fully qualified component type and scene ID. Paginated, at most 100 objects. Returns exact IDs, hierarchy, transforms, script GUIDs and prefab source GUIDs.")]
    public Task<JsonElement> FindObjects(string query = "", string componentType = "", string sceneId = "", int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        => Send(new() { action = "find_objects", query = query, typeName = componentType, sceneId = sceneId, offset = offset, limit = limit }, cancellationToken);

    [McpServerTool(Name = "unity_inspect_object"), Description("Read current state of one GameObject ID, including components and the latest revision needed for edits. Names are not IDs.")]
    public Task<JsonElement> InspectObject(string objectId, CancellationToken cancellationToken = default)
        => Send(new() { action = "inspect_object", objectId = objectId }, cancellationToken);

    [McpServerTool(Name = "unity_get_component_properties"), Description("Read Inspector serialized property paths, types, values and editability for a component ID. Object reference values are IDs. Results are paginated; unsupported types are read-only. Transform changes use unity_set_transform.")]
    public Task<JsonElement> GetProperties(string componentId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        => Send(new() { action = "get_properties", componentId = componentId, offset = offset, limit = limit }, cancellationToken);

    [McpServerTool(Name = "unity_find_assets"), Description("Search Unity's asset catalog using AssetDatabase filters, e.g. t:Prefab Enemy or t:Material. Returns GUIDs, paths and types, without embeddings or bulk loading. Use GUIDs for prefab placement.")]
    public Task<JsonElement> FindAssets(string query = "", int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        => Send(new() { action = "find_assets", query = query, offset = offset, limit = limit }, cancellationToken);

    [McpServerTool(Name = "unity_inspect_asset"), Description("Read asset labels, up to 200 direct dependencies and main/subasset reference IDs by GUID. Use returned reference IDs for component property assignments. Does not open or modify scenes.")]
    public Task<JsonElement> InspectAsset(string assetGuid, CancellationToken cancellationToken = default)
        => Send(new() { action = "inspect_asset", assetGuid = assetGuid }, cancellationToken);

    [McpServerTool(Name = "unity_get_diagnostics"), Description("Read bounded Unity compilation diagnostics and runtime exceptions observed by the bridge. Unknown status means no compile has been observed, not zero errors. Does not trigger compilation or run the game.")]
    public Task<JsonElement> GetDiagnostics(CancellationToken cancellationToken = default) => Send(new() { action = "get_diagnostics" }, cancellationToken);

    [McpServerTool(Name = "unity_create_game_object"), Description("Create an Empty, Cube, Sphere, Capsule, Cylinder, Plane or Quad in an explicit loaded scene. Requires current session/revision from a read and a unique requestId; retry timeouts with identical ID/arguments. Undoable, Edit Mode only, leaves scene unsaved. Positions are local unless worldSpace=true; scale is always local.")]
    public Task<JsonElement> CreateObject(string session, long expectedRevision, string requestId, string sceneId, string name,
        string primitive = "Empty", string parentId = "", float[]? position = null, float[]? rotation = null, float[]? scale = null,
        bool worldSpace = false, CancellationToken cancellationToken = default)
    {
        var r = Write("create", session, expectedRevision, requestId);
        r.sceneId = sceneId; r.name = name; r.typeName = primitive; r.parentId = parentId;
        r.position = position; r.rotation = rotation; r.scale = scale; r.worldSpace = worldSpace;
        return Send(r, cancellationToken);
    }
    [McpServerTool(Name = "unity_instantiate_prefab"), Description("Instantiate a prefab asset GUID into an explicit scene, preserving its prefab link. Undoable, unsaved, Edit Mode only. Supply current session/revision and a unique requestId. Reuse identical arguments/ID after timeout. Optional parent must be in that scene. Euler rotation is in degrees; scale is local.")]
    public Task<JsonElement> InstantiatePrefab(string session, long expectedRevision, string requestId, string sceneId, string assetGuid,
        string parentId = "", float[]? position = null, float[]? rotation = null, float[]? scale = null, bool worldSpace = false, CancellationToken cancellationToken = default)
    {
        var r = Write("instantiate_prefab", session, expectedRevision, requestId);
        r.sceneId = sceneId; r.assetGuid = assetGuid; r.parentId = parentId;
        r.position = position; r.rotation = rotation; r.scale = scale; r.worldSpace = worldSpace;
        return Send(r, cancellationToken);
    }
    [McpServerTool(Name = "unity_set_transform"), Description("Undoable Transform edit by object ID. Vectors have three finite numbers; omitted values remain unchanged. Position/Euler rotation use local space unless worldSpace=true; scale always local. Requires latest read session/revision and a unique requestId.")]
    public Task<JsonElement> SetTransform(string session, long expectedRevision, string requestId, string objectId,
        float[]? position = null, float[]? rotation = null, float[]? scale = null, bool worldSpace = false, CancellationToken cancellationToken = default)
    {
        var r = Write("set_transform", session, expectedRevision, requestId);
        r.objectId = objectId; r.position = position; r.rotation = rotation; r.scale = scale; r.worldSpace = worldSpace;
        return Send(r, cancellationToken);
    }
    [McpServerTool(Name = "unity_set_parent"), Description("Reparent an object in the same loaded scene, preserving world Transform. Empty parentId moves to scene root. Rejects cycles and prefab-internal restructuring. Requires latest session/revision and unique requestId; Undoable, unsaved.")]
    public Task<JsonElement> SetParent(string session, long expectedRevision, string requestId, string objectId, string parentId = "", CancellationToken cancellationToken = default)
    {
        var r = Write("set_parent", session, expectedRevision, requestId); r.objectId = objectId; r.parentId = parentId;
        return Send(r, cancellationToken);
    }
    [McpServerTool(Name = "unity_add_component"), Description("Add a compiled Component by fully qualified type name to an object ID. Unity lifecycle callbacks may run. Requires latest session/revision and unique requestId. Returns component IDs for subsequent property inspection; Undoable, Edit Mode only.")]
    public Task<JsonElement> AddComponent(string session, long expectedRevision, string requestId, string objectId, string typeName, CancellationToken cancellationToken = default)
    {
        var r = Write("add_component", session, expectedRevision, requestId); r.objectId = objectId; r.typeName = typeName;
        return Send(r, cancellationToken);
    }
    [McpServerTool(Name = "unity_set_component_properties"), Description("Set 1-32 distinct Inspector properties on a component after inspecting them. Values are strings: invariant numbers, true/false, exact enum name, x,y,z for Vector3, or an object reference ID (empty clears it). Unsupported/protected fields are rejected. Requires latest session/revision and unique requestId. Undoable, leaves scene unsaved.")]
    public Task<JsonElement> SetProperties(string session, long expectedRevision, string requestId, string componentId,
        UnityPropertyEdit[] properties, CancellationToken cancellationToken = default)
    {
        var r = Write("set_properties", session, expectedRevision, requestId); r.componentId = componentId;
        r.properties = properties.Select(p => new PropertyEdit { path = p.Path, value = p.Value }).ToArray();
        return Send(r, cancellationToken);
    }
}
public sealed record UnityPropertyEdit(string Path, string Value);
