// Copy this file to Assets/Editor/LavenderOpenAsset.cs in your Unity project.
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using Unity.CodeEditor;

public static class LavenderOpenAsset
{
    private const string ExecutableKey = "Lavender.ExternalEditor.Executable";
    private const string EnabledKey = "Lavender.ExternalEditor.Enabled";
    internal const string PreviousEditorKey = "Lavender.ExternalEditor.PreviousEditor";

    [InitializeOnLoadMethod]
    private static void RegisterEditor()
    {
        CodeEditor.Register(new LavenderCodeEditor());
    }

    [MenuItem("Tools/Lavender/Choose Lavender executable...")]
    private static void ChooseExecutable()
    {
        string path = EditorUtility.OpenFilePanel("Choose Lavender.exe", "", "exe");
        if (string.IsNullOrEmpty(path)) return;

        if (!string.Equals(CodeEditor.CurrentEditorPath, EditorPrefs.GetString(ExecutableKey, ""), StringComparison.OrdinalIgnoreCase))
            EditorPrefs.SetString(PreviousEditorKey, CodeEditor.CurrentEditorPath);
        EditorPrefs.SetString(ExecutableKey, path);
        EditorPrefs.SetBool(EnabledKey, true);
        CodeEditor.SetExternalScriptEditor(path);
        UnityEngine.Debug.Log("Lavender is now Unity's external script editor. Double-click a Console source location to open it.");
    }

    [MenuItem("Tools/Lavender/Enable source navigation")]
    private static void ToggleEnabled()
    {
        bool enabled = !EditorPrefs.GetBool(EnabledKey, false);
        string path = EditorPrefs.GetString(ExecutableKey, "");
        if (enabled && !File.Exists(path)) { ChooseExecutable(); return; }
        EditorPrefs.SetBool(EnabledKey, enabled);
        if (enabled)
        {
            if (!string.Equals(CodeEditor.CurrentEditorPath, path, StringComparison.OrdinalIgnoreCase))
                EditorPrefs.SetString(PreviousEditorKey, CodeEditor.CurrentEditorPath);
            CodeEditor.SetExternalScriptEditor(path);
        }
        else if (string.Equals(CodeEditor.CurrentEditorPath, path, StringComparison.OrdinalIgnoreCase))
            CodeEditor.SetExternalScriptEditor(EditorPrefs.GetString(PreviousEditorKey, ""));
    }

    [MenuItem("Tools/Lavender/Enable source navigation", true)]
    private static bool ValidateEnabled()
    {
        Menu.SetChecked("Tools/Lavender/Enable source navigation", EditorPrefs.GetBool(EnabledKey, false));
        return true;
    }

    [OnOpenAsset(-100)]
    public static bool OpenAsset(int instanceId, int line, int column)
    {
        if (!EditorPrefs.GetBool(EnabledKey, false)) return false;
        string assetPath = AssetDatabase.GetAssetPath(instanceId);

        return OpenPath(assetPath, line, column);
    }

    internal static string Executable => EditorPrefs.GetString(ExecutableKey, "");

    internal static bool OpenPath(string assetPath, int line, int column)
    {

        if (!string.Equals(Path.GetExtension(assetPath), ".cs", StringComparison.OrdinalIgnoreCase)) return false;
        string executable = EditorPrefs.GetString(ExecutableKey, "");

        if (!File.Exists(executable))
        {
            UnityEngine.Debug.LogWarning("Lavender executable is missing. Use Tools > Lavender > Choose Lavender executable...");
            return false;
        }

        try
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string file = Path.GetFullPath(Path.Combine(project, assetPath));

            // Registry packages have virtual Packages/... paths; resolve their actual disk location.
            if (!File.Exists(file))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (package != null && assetPath.StartsWith(package.assetPath + "/", StringComparison.Ordinal))
                {
                    file = Path.GetFullPath(Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length + 1)));
                }
            }
            if (!File.Exists(file)) return false;

            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--project " + Quote(project) + " --file " + Quote(file)
                    + " --line " + Math.Max(1, line) + " --column " + Math.Max(1, column),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                return process != null;
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("Could not open Lavender: " + ex.Message);
            return false; // Let Unity's usual external editor handle it.
        }
    }

    // Windows paths cannot contain quotes. Escape trailing slashes before the closing quote.
    private static string Quote(string path)
    {
        if (path.IndexOf('"') >= 0)
        {
            throw new ArgumentException("Invalid path.");
        }

        int trailing = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailing) + "\"";
    }
}

// Console navigation can invoke the selected external editor directly, bypassing asset callbacks.
public sealed class LavenderCodeEditor : IExternalCodeEditor
{
    public CodeEditor.Installation[] Installations => File.Exists(LavenderOpenAsset.Executable)
        ? new[] { new CodeEditor.Installation { Name = "Lavender", Path = LavenderOpenAsset.Executable } }
        : new CodeEditor.Installation[0];

    public bool TryGetInstallationForPath(string path, out CodeEditor.Installation installation)
    {
        installation = new CodeEditor.Installation { Name = "Lavender", Path = path };
        return !string.IsNullOrEmpty(path) && string.Equals(path, LavenderOpenAsset.Executable, StringComparison.OrdinalIgnoreCase);
    }

    public bool OpenProject(string path, int line, int column) => LavenderOpenAsset.OpenPath(path, line, column);
    public void OnGUI() => EditorGUILayout.HelpBox("Lavender receives script and Console source locations. Configure it under Tools > Lavender.", MessageType.Info);

    // Preserve the previously selected integration's project-file generation.
    private IExternalCodeEditor ProjectGenerator
    {
        get
        {
            string previous = EditorPrefs.GetString(LavenderOpenAsset.PreviousEditorKey, "");
            if (string.IsNullOrEmpty(previous) || string.Equals(previous, LavenderOpenAsset.Executable, StringComparison.OrdinalIgnoreCase)) return null;
            var editor = CodeEditor.Editor.GetCodeEditorForPath(previous);
            return editor is LavenderCodeEditor ? null : editor;
        }
    }
    public void Initialize(string path)
    {
        ProjectGenerator?.Initialize(EditorPrefs.GetString(LavenderOpenAsset.PreviousEditorKey, ""));
    }
    public void SyncAll() => ProjectGenerator?.SyncAll();
    public void SyncIfNeeded(string[] added, string[] deleted, string[] moved, string[] movedFrom, string[] imported)
        => ProjectGenerator?.SyncIfNeeded(added, deleted, moved, movedFrom, imported);
}
#endif
