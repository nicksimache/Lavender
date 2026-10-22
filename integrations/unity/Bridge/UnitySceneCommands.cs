#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lavender.UnityBridge
{
    public static class UnitySceneCommands
    {
        private static string Root;
        private static readonly string Session = Guid.NewGuid().ToString("N");
        private static long Revision = 1;
        private static readonly Dictionary<string, Tuple<string, BridgeResponse>> Receipts = new Dictionary<string, Tuple<string, BridgeResponse>>();
        private static readonly List<DiagnosticInfo> Diagnostics = new List<DiagnosticInfo>();
        private static string CompilationStatus = "unknown_until_next_compilation";
        private static bool Initialized;

        public static void Initialize(string root)
        {
            if (Initialized) return;
            Initialized = true;
            Root = root;
            EditorApplication.hierarchyChanged += Changed;
            EditorApplication.projectChanged += Changed;
            Undo.undoRedoPerformed += Changed;
            ObjectChangeEvents.changesPublished += ObjectChanges;
            EditorSceneManager.sceneOpened += (scene, mode) => Changed();
            EditorSceneManager.sceneClosed += scene => Changed();
            EditorSceneManager.sceneSaved += scene => Changed();
            CompilationPipeline.compilationStarted += _ => { Diagnostics.Clear(); CompilationStatus = "compiling"; Changed(); };
            CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
            {
                foreach (var message in messages.Take(200))
                    AddDiagnostic(new DiagnosticInfo { severity = message.type.ToString(), message = message.message,
                        file = message.file, line = message.line, column = message.column });
            };
            CompilationPipeline.compilationFinished += _ =>
            {
                CompilationStatus = Diagnostics.Any(d => d.severity == "Error") ? "failed" : "completed";
                SessionState.SetString("Lavender.Unity.Compilation", JsonUtility.ToJson(new BridgeResponse
                    { diagnostics = Diagnostics.ToArray(), compilationStatus = CompilationStatus }));
                Changed();
            };
            string previous = SessionState.GetString("Lavender.Unity.Compilation", "");
            if (!string.IsNullOrEmpty(previous))
            {
                var snapshot = JsonUtility.FromJson<BridgeResponse>(previous);
                Diagnostics.AddRange(snapshot.diagnostics ?? new DiagnosticInfo[0]);
                CompilationStatus = "last_compilation_" + snapshot.compilationStatus;
            }
            Application.logMessageReceived += (message, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                    AddDiagnostic(new DiagnosticInfo { severity = type.ToString(), message = Clip(message), stack = Clip(stack) });
            };
        }
        private static void AddDiagnostic(DiagnosticInfo entry)
        {
            if (Diagnostics.Count >= 200) Diagnostics.RemoveAt(0);
            Diagnostics.Add(entry);
        }
        private static string Clip(string text) => text == null ? "" : text.Substring(0, Math.Min(text.Length, 4000));
        private static void Changed() => Revision++;
        private static void ObjectChanges(ref ObjectChangeEventStream stream) => Changed();
        private static bool IsWrite(string action) => action == "create" || action == "instantiate_prefab" ||
            action == "set_transform" || action == "set_parent" || action == "add_component" || action == "set_properties";

        public static BridgeResponse Execute(BridgeRequest request)
        {
            bool write = request != null && IsWrite(request.action);
            string signature = request == null ? "" : JsonUtility.ToJson(request);
            try
            {
                if (request == null || request.version != BridgeProtocol.Version) throw new InvalidOperationException("Unsupported bridge protocol.");
                if (string.IsNullOrEmpty(request.projectPath) || BridgeProtocol.CanonicalPath(request.projectPath) != BridgeProtocol.CanonicalPath(Root))
                    throw new InvalidOperationException("Unity project does not match the selected Lavender project.");
                if (write)
                {
                    if (request.session != Session) throw new InvalidOperationException("Unity session changed. Read editor state again before editing.");
                    if (string.IsNullOrWhiteSpace(request.requestId) || request.requestId.Length > 128)
                        throw new InvalidOperationException("A unique requestId (max 128 characters) is required.");
                    if (Receipts.TryGetValue(request.requestId, out var receipt))
                    {
                        if (signature != receipt.Item1) throw new InvalidOperationException("requestId was already used for different arguments.");
                        return receipt.Item2;
                    }
                    if (Receipts.Count >= 4096) throw new InvalidOperationException("Session command limit reached; reload the Unity domain before further edits.");
                    if (request.expectedRevision != Revision) throw new InvalidOperationException("Unity state changed. Inspect the target again and use its latest revision.");
                    if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                        throw new InvalidOperationException("Edits require idle Edit Mode (not playing, compiling or importing).");
                    if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                        throw new InvalidOperationException("Exit Prefab Mode before scene editing. This version edits loaded scenes only.");
                }
                var response = State();
                switch (request.action)
                {
                    case "get_state":
                        response.scenes = Scenes().Select(SceneSnapshot).ToArray();
                        response.objects = Selection.gameObjects.Where(g => !EditorUtility.IsPersistent(g)).Take(50).Select(Snapshot).ToArray();
                        break;
                    case "find_objects":
                        var objects = Objects().Where(g => string.IsNullOrEmpty(request.sceneId) || g.scene.handle.ToString() == request.sceneId)
                            .Where(g => string.IsNullOrEmpty(request.query) || g.name.IndexOf(request.query, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Where(g => string.IsNullOrEmpty(request.typeName) || g.GetComponents<Component>().Any(c => c != null && c.GetType().FullName == request.typeName))
                            .OrderBy(g => g.GetInstanceID()).ToArray();
                        response.objects = Page(objects, request, response).Select(Snapshot).ToArray();
                        break;
                    case "inspect_object": response.objects = new[] { Snapshot(ResolveGameObject(request.objectId)) }; break;
                    case "get_properties":
                        using (var serialized = new SerializedObject(ResolveComponent(request.componentId)))
                        {
                            var properties = new List<PropertyInfo>();
                            var property = serialized.GetIterator();
                            while (property.NextVisible(true))
                            {
                                if (properties.Count >= 5000) break;
                                properties.Add(PropertySnapshot(property));
                            }
                            response.properties = Page(properties.ToArray(), request, response).ToArray();
                        }
                        break;
                    case "find_assets":
                        var guids = AssetDatabase.FindAssets(request.query ?? "").OrderBy(g => g, StringComparer.Ordinal).ToArray();
                        response.assets = Page(guids, request, response).Select(g => AssetSnapshot(g, false)).ToArray();
                        break;
                    case "inspect_asset": response.assets = new[] { AssetSnapshot(request.assetGuid, true) }; break;
                    case "get_diagnostics": response.diagnostics = Diagnostics.ToArray(); break;
                    default:
                        if (!write) throw new InvalidOperationException("Unknown Unity command.");
                        response.objects = new[] { Mutate(request) };
                        break;
                }
                response.revision = Revision;
                response.success = true;
                if (write) Receipts.Add(request.requestId, Tuple.Create(signature, response));
                return response;
            }
            catch (Exception ex)
            {
                var response = State();
                response.error = ex.Message;
                return response;
            }
        }
        private static BridgeResponse State() => new BridgeResponse
        {
            session = Session, revision = Revision, projectPath = Root, unityVersion = Application.unityVersion,
            playing = EditorApplication.isPlayingOrWillChangePlaymode, compiling = EditorApplication.isCompiling,
            activeSceneId = SceneManager.GetActiveScene().handle.ToString(),
            prefabStage = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath,
            compilationStatus = CompilationStatus
        };
        private static IEnumerable<Scene> Scenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene)) yield return scene;
            }
        }
        private static IEnumerable<GameObject> Objects() => Scenes().SelectMany(s => s.GetRootGameObjects())
            .SelectMany(g => g.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject);
        private static IEnumerable<T> Page<T>(T[] items, BridgeRequest request, BridgeResponse response)
        {
            if (request.offset < 0 || request.limit < 1 || request.limit > 100) throw new ArgumentException("Use offset >= 0 and limit between 1 and 100.");
            response.total = items.Length;
            response.nextOffset = Math.Min(items.Length, request.offset + request.limit);
            response.hasMore = response.nextOffset < items.Length;
            return items.Skip(request.offset).Take(request.limit);
        }
        private static string Id(Object obj)
        {
            if (obj == null) return "";
            if (EditorUtility.IsPersistent(obj) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long local))
                return "asset:" + guid + ":" + local;
            return Session + ":" + obj.GetInstanceID();
        }
        private static Object Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Object ID is required.");
            if (id.StartsWith("asset:", StringComparison.Ordinal))
            {
                var parts = id.Split(':');
                if (parts.Length != 3 || !long.TryParse(parts[2], out long local)) throw new ArgumentException("Invalid asset reference ID.");
                string path = AssetDatabase.GUIDToAssetPath(parts[1]);
                var asset = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(a =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string guid, out long file) && file == local);
                if (asset == null) throw new InvalidOperationException("Asset reference no longer exists.");
                return asset;
            }
            string prefix = Session + ":";
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(id.Substring(prefix.Length), out int instance))
                throw new InvalidOperationException("Object ID belongs to an old Unity session.");
            var obj = EditorUtility.InstanceIDToObject(instance);
            if (obj == null || EditorUtility.IsPersistent(obj)) throw new InvalidOperationException("Scene object no longer exists.");
            return obj;
        }
        private static GameObject ResolveGameObject(string id)
        {
            var go = Resolve(id) as GameObject;
            if (go == null || EditorUtility.IsPersistent(go) || !Scenes().Any(s => s.handle == go.scene.handle))
                throw new InvalidOperationException("Expected a GameObject in a loaded scene.");
            return go;
        }
        private static Component ResolveComponent(string id)
        {
            var component = Resolve(id) as Component;
            if (component == null) throw new InvalidOperationException("Component no longer exists.");
            ResolveGameObject(Id(component.gameObject));
            return component;
        }
        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static Vector3 Vector(float[] values)
        {
            if (values == null || values.Length != 3 || values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new ArgumentException("Vectors require three finite numbers.");
            return new Vector3(values[0], values[1], values[2]);
        }
        private static SceneInfo SceneSnapshot(Scene s) => new SceneInfo
        { id = s.handle.ToString(), path = s.path, name = s.name, active = s == SceneManager.GetActiveScene(), dirty = s.isDirty };
        private static ComponentInfo ComponentSnapshot(Component c) => c == null ? new ComponentInfo { missing = true } : new ComponentInfo
        {
            id = Id(c), type = c.GetType().FullName,
            scriptGuid = c is MonoBehaviour behaviour ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(behaviour))) : ""
        };
        private static ObjectInfo Snapshot(GameObject g) => new ObjectInfo
        {
            id = Id(g), globalId = GlobalObjectId.GetGlobalObjectIdSlow(g).ToString(), name = g.name,
            parentId = g.transform.parent == null ? "" : Id(g.transform.parent.gameObject), sceneId = g.scene.handle.ToString(),
            tag = g.tag, layer = g.layer, siblingIndex = g.transform.GetSiblingIndex(), activeSelf = g.activeSelf, activeInHierarchy = g.activeInHierarchy,
            localPosition = V(g.transform.localPosition), worldPosition = V(g.transform.position), localRotation = V(g.transform.localEulerAngles),
            localScale = V(g.transform.localScale), components = g.GetComponents<Component>().Select(ComponentSnapshot).ToArray(),
            prefabGuid = AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g))
        };
        private static AssetInfo AssetSnapshot(string guid, bool details)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Asset GUID no longer exists.");
            return new AssetInfo { guid = guid, path = path, type = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName,
                labels = details ? AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path)) : null,
                references = details ? AssetDatabase.LoadAllAssetsAtPath(path).Where(a => a != null).Take(200)
                    .Select(a => new AssetReference { id = Id(a), name = a.name, type = a.GetType().FullName }).ToArray() : null,
                dependencies = details ? AssetDatabase.GetDependencies(path, false).Take(200).ToArray() : null };
        }
        private static bool Editable(SerializedProperty p) => p.editable && !new[] { "m_Script", "m_GameObject", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset" }.Contains(p.propertyPath)
            && (p.propertyType == SerializedPropertyType.Boolean || p.propertyType == SerializedPropertyType.Integer ||
                p.propertyType == SerializedPropertyType.Float || p.propertyType == SerializedPropertyType.String ||
                p.propertyType == SerializedPropertyType.Enum || p.propertyType == SerializedPropertyType.ObjectReference ||
                p.propertyType == SerializedPropertyType.Vector3);
        private static PropertyInfo PropertySnapshot(SerializedProperty p)
        {
            string value;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: value = p.boolValue.ToString(); break;
                case SerializedPropertyType.Integer: value = p.longValue.ToString(CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Float: value = p.doubleValue.ToString(CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.String: value = Clip(p.stringValue); break;
                case SerializedPropertyType.Enum: value = p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString(); break;
                case SerializedPropertyType.ObjectReference: value = Id(p.objectReferenceValue); break;
                case SerializedPropertyType.Vector3: value = string.Join(",", V(p.vector3Value).Select(v => v.ToString(CultureInfo.InvariantCulture))); break;
                default: value = p.isArray ? "Array size: " + p.arraySize : "Inspect child properties"; break;
            }
            return new PropertyInfo { path = p.propertyPath, type = p.propertyType.ToString(), value = value,
                editable = !(p.serializedObject.targetObject is Transform) && Editable(p) };
        }
        private static void SetProperty(SerializedProperty p, string value)
        {
            if (!Editable(p)) throw new ArgumentException("Unsupported or protected property: " + p.propertyPath);
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: p.boolValue = bool.Parse(value); break;
                case SerializedPropertyType.Integer: p.longValue = long.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Float:
                    double number = double.Parse(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Number must be finite.");
                    p.doubleValue = number; break;
                case SerializedPropertyType.String: if (value.Length > 4000) throw new ArgumentException("String is too long."); p.stringValue = value; break;
                case SerializedPropertyType.Enum:
                    int index = Array.IndexOf(p.enumNames, value);
                    if (index < 0) throw new ArgumentException("Use an exact enum name: " + string.Join(", ", p.enumNames));
                    p.enumValueIndex = index; break;
                case SerializedPropertyType.ObjectReference:
                    Object reference = string.IsNullOrEmpty(value) ? null : Resolve(value);
                    p.objectReferenceValue = reference;
                    if (p.objectReferenceValue != reference) throw new ArgumentException("Object reference is incompatible with this property.");
                    break;
                case SerializedPropertyType.Vector3:
                    p.vector3Value = Vector(value.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()); break;
            }
        }
        private static ObjectInfo Mutate(BridgeRequest r)
        {
            if (r.position != null && r.position.Length > 0) Vector(r.position);
            if (r.rotation != null && r.rotation.Length > 0) Vector(r.rotation);
            if (r.scale != null && r.scale.Length > 0) Vector(r.scale);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Lavender " + r.action);
            GameObject go = null;
            try
            {
                if (r.action == "create" || r.action == "instantiate_prefab")
                {
                    var scene = Scenes().FirstOrDefault(s => s.handle.ToString() == r.sceneId);
                    if (!scene.IsValid()) throw new ArgumentException("Specify a loaded scene ID from get_state.");
                    GameObject parent = string.IsNullOrEmpty(r.parentId) ? null : ResolveGameObject(r.parentId);
                    if (parent != null && parent.scene != scene) throw new ArgumentException("Parent must belong to the target scene.");
                    if (r.action == "instantiate_prefab")
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(r.assetGuid));
                        if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab)) throw new ArgumentException("assetGuid must identify a prefab or model prefab.");
                        go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(r.name) || r.name.Length > 200) throw new ArgumentException("A name of 1-200 characters is required.");
                        if (string.IsNullOrEmpty(r.typeName) || r.typeName == "Empty") go = new GameObject(r.name);
                        else
                        {
                            if (!Enum.TryParse(r.typeName, out PrimitiveType primitive) || !Enum.IsDefined(typeof(PrimitiveType), primitive))
                                throw new ArgumentException("Use Empty, Cube, Sphere, Capsule, Cylinder, Plane or Quad.");
                            go = GameObject.CreatePrimitive(primitive);
                            go.name = r.name;
                        }
                        SceneManager.MoveGameObjectToScene(go, scene);
                    }
                    Undo.RegisterCreatedObjectUndo(go, "Lavender create");
                    if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Lavender parent");
                    ApplyTransform(go, r);
                }
                else
                {
                    var component = r.action == "set_properties" ? ResolveComponent(r.componentId) : null;
                    go = component != null ? component.gameObject : ResolveGameObject(r.objectId);
                    switch (r.action)
                    {
                        case "set_transform": ApplyTransform(go, r); break;
                        case "set_parent":
                            var parent = string.IsNullOrEmpty(r.parentId) ? null : ResolveGameObject(r.parentId);
                            if (parent != null && (parent.scene != go.scene || parent == go || parent.transform.IsChildOf(go.transform)))
                                throw new ArgumentException("Parent must be in the same scene and cannot create a hierarchy cycle.");
                            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                                throw new InvalidOperationException("Cannot reparent a child inside a prefab instance.");
                            Undo.SetTransformParent(go.transform, parent == null ? null : parent.transform, "Lavender parent");
                            break;
                        case "add_component":
                            var types = TypeCache.GetTypesDerivedFrom<Component>().Where(t => t.FullName == r.typeName && !t.IsAbstract && !t.ContainsGenericParameters).ToArray();
                            if (types.Length != 1 || typeof(Transform).IsAssignableFrom(types[0])) throw new ArgumentException("Specify one fully qualified, concrete Component type (not Transform).");
                            if (Undo.AddComponent(go, types[0]) == null) throw new InvalidOperationException("Unity rejected the component.");
                            break;
                        case "set_properties":
                            if (component is Transform) throw new ArgumentException("Use set_transform for Transform edits.");
                            if (r.properties == null || r.properties.Length < 1 || r.properties.Length > 32 || r.properties.Select(p => p.path).Distinct().Count() != r.properties.Length)
                                throw new ArgumentException("Provide 1-32 distinct property edits.");
                            using (var serialized = new SerializedObject(component))
                            {
                                serialized.Update();
                                // Stage every assignment before applying; invalid input leaves the target unchanged.
                                foreach (var edit in r.properties)
                                    SetProperty(serialized.FindProperty(edit.path) ?? throw new ArgumentException("Unknown property: " + edit.path), edit.value ?? "");
                                Undo.RecordObject(component, "Lavender properties");
                                serialized.ApplyModifiedProperties();
                            }
                            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                            break;
                    }
                }
                EditorSceneManager.MarkSceneDirty(go.scene);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                Changed();
                return Snapshot(go);
            }
            catch
            {
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(group);
                Changed();
                throw;
            }
        }
        private static void ApplyTransform(GameObject go, BridgeRequest r)
        {
            Undo.RecordObject(go.transform, "Lavender transform");
            if (r.position != null && r.position.Length > 0)
            {
                if (r.worldSpace) go.transform.position = Vector(r.position); else go.transform.localPosition = Vector(r.position);
            }
            if (r.rotation != null && r.rotation.Length > 0)
            {
                if (r.worldSpace) go.transform.eulerAngles = Vector(r.rotation); else go.transform.localEulerAngles = Vector(r.rotation);
            }
            if (r.scale != null && r.scale.Length > 0) go.transform.localScale = Vector(r.scale);
            if (PrefabUtility.IsPartOfPrefabInstance(go.transform)) PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        }
    }
}
#endif
