#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Lavender.UnityBridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BridgeSmokeTests
{
    static string root;
    static int passed;
    public static void Serve()
    {
        root = Directory.GetParent(Application.dataPath).FullName;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        File.WriteAllText(Path.Combine(root, "client-ready.txt"), BridgeProtocol.PipeName(root) + "\n" + BridgeProtocol.CanonicalPath(root) + "\n" + Environment.UserName + ":" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        double deadline = EditorApplication.timeSinceStartup + 120;
        EditorApplication.update += () =>
        {
            if (File.Exists(Path.Combine(root, "client-done.txt"))) EditorApplication.Exit(0);
            else if (EditorApplication.timeSinceStartup > deadline) EditorApplication.Exit(1);
        };
    }
    public static async void Run()
    {
        root = Directory.GetParent(Application.dataPath).FullName;
        try
        {
            UnitySceneCommands.Initialize(root);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var create = Write("create"); create.sceneId = scene.handle.ToString(); create.name = "Fixture";
            var created = Call(create); Check(created.success, "create: " + created.error);
            string id = created.objects[0].id;
            Check(Call(create).success && scene.rootCount == 1, "Identical retry creates no duplicate");
            create.name = "Different"; Check(!Call(create).success && scene.rootCount == 1, "Request ID cannot be reused with different payload");
            var stale = Write("create"); stale.sceneId = scene.handle.ToString(); stale.name = "Stale"; stale.expectedRevision--;
            Check(!Call(stale).success && scene.rootCount == 1, "Stale revision rejected");
            var add = Write("add_component"); add.objectId = id; add.typeName = "UnityEngine.BoxCollider";
            var added = Call(add); Check(added.success, "Add component: " + added.error);
            string collider = added.objects[0].components.Single(c => c.type == "UnityEngine.BoxCollider").id;
            var edit = Write("set_properties"); edit.componentId = collider;
            edit.properties = new[] { new PropertyEdit { path = "m_IsTrigger", value = "true" } };
            Check(Call(edit).success && scene.GetRootGameObjects()[0].GetComponent<BoxCollider>().isTrigger, "Serialized component edit");
            edit = Write("set_properties"); edit.componentId = collider;
            edit.properties = new[] { new PropertyEdit { path = "m_IsTrigger", value = "false" }, new PropertyEdit { path = "m_GameObject", value = "" } };
            Check(!Call(edit).success && scene.GetRootGameObjects()[0].GetComponent<BoxCollider>().isTrigger, "Invalid property batch leaves previous values intact");
            var move = Write("set_transform"); move.objectId = id; move.position = new[] { 2f, 3f, 4f };
            Check(Call(move).success && scene.GetRootGameObjects()[0].transform.position == new Vector3(2, 3, 4), "Transform edit");
            var parent = Write("set_parent"); parent.objectId = id; parent.parentId = id;
            Check(!Call(parent).success, "Hierarchy cycle rejected");
            var source = scene.GetRootGameObjects()[0];
            source.SetActive(false);
            Check(Call(new BridgeRequest { action = "find_objects", query = "Fixture", limit = 10 }).objects.Length == 1, "Inactive objects are indexed");
            PrefabUtility.SaveAsPrefabAsset(source, "Assets/Fixture.prefab");
            var prefab = Write("instantiate_prefab"); prefab.sceneId = scene.handle.ToString(); prefab.assetGuid = AssetDatabase.AssetPathToGUID("Assets/Fixture.prefab");
            var spawned = Call(prefab); Check(spawned.success, "Prefab spawn: " + spawned.error);
            var instance = scene.GetRootGameObjects().Single(g => g != source);
            Check(PrefabUtility.IsPartOfPrefabInstance(instance), "Prefab connection retained");
            Undo.PerformUndo(); Check(instance == null && scene.rootCount == 1, "Undo removes created instance");
            Check(Call(new BridgeRequest { action = "find_assets", query = "t:Prefab", limit = 10 }).assets.Any(a => a.guid == prefab.assetGuid), "Asset catalog returns prefab GUID");
            using (var timeout = new CancellationTokenSource(10000))
            using (var pipe = new NamedPipeClientStream(".", BridgeProtocol.PipeName(root), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(2000, timeout.Token);
                await BridgeProtocol.WriteAsync(pipe, JsonUtility.ToJson(new BridgeRequest { action = "get_state", projectPath = root }), timeout.Token);
                var response = JsonUtility.FromJson<BridgeResponse>(await BridgeProtocol.ReadAsync(pipe, timeout.Token));
                Check(response.success && response.scenes.Length == 1, "Named-pipe request dispatched on editor thread");
            }
            File.WriteAllText(Path.Combine(root, "smoke-results.txt"), "PASS: " + passed + " checks");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "smoke-results.txt"), "FAIL after " + passed + " checks: " + ex);
            Debug.LogException(ex); EditorApplication.Exit(1);
        }
    }
    static BridgeRequest Write(string action)
    {
        var state = Call(new BridgeRequest { action = "get_state" });
        return new BridgeRequest { action = action, session = state.session, expectedRevision = state.revision, requestId = Guid.NewGuid().ToString("N") };
    }
    static BridgeResponse Call(BridgeRequest request)
    {
        request.projectPath = root;
        return UnitySceneCommands.Execute(JsonUtility.FromJson<BridgeRequest>(JsonUtility.ToJson(request)));
    }
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        passed++; Debug.Log("PASS: " + label);
    }
}
#endif
