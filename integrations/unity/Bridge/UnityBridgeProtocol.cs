#nullable disable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lavender.UnityBridge
{
    public static class BridgeProtocol
    {
        public const int Version = 1;
        public const int MaxBytes = 2 * 1024 * 1024;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        private static uint DesktopSession()
        {
            // Mono's Process.SessionId returns 0 even in an interactive Windows session.
            if (!ProcessIdToSessionId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, out uint session))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return session;
        }
        public static string CanonicalPath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant();
        public static string PipeName(string project)
        {
            using (var sha = SHA256.Create())
                return "Lavender.Unity." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    Environment.UserName + ":" + DesktopSession() + ":" + CanonicalPath(project)))).Replace("-", "");
        }
        public static async Task WriteAsync(Stream stream, string json, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaxBytes) throw new IOException("Bridge message is too large; request a smaller page.");
            byte[] size = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(size, 0, size.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        public static async Task<string> ReadAsync(Stream stream, CancellationToken token)
        {
            var size = new byte[4];
            await ReadExactly(stream, size, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(size, 0);
            if (length < 1 || length > MaxBytes) throw new IOException("Invalid bridge frame length.");
            var bytes = new byte[length];
            await ReadExactly(stream, bytes, token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        private static async Task ReadExactly(Stream stream, byte[] data, CancellationToken token)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int count = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Unity bridge disconnected.");
                offset += count;
            }
        }
    }
    [Serializable] public sealed class BridgeRequest
    {
        public int version = BridgeProtocol.Version;
        public string projectPath, requestId, session, action, objectId, componentId, sceneId, parentId, assetGuid, name, typeName, query;
        public long expectedRevision = -1;
        public int offset, limit = 50;
        public float[] position, rotation, scale;
        public bool worldSpace;
        public PropertyEdit[] properties;
    }
    [Serializable] public sealed class PropertyEdit { public string path, value; }
    [Serializable] public sealed class BridgeResponse
    {
        public int version = BridgeProtocol.Version;
        public bool success, playing, compiling, hasMore;
        public string error, session, projectPath, unityVersion, activeSceneId, prefabStage, compilationStatus;
        public long revision;
        public int total, nextOffset;
        public SceneInfo[] scenes;
        public ObjectInfo[] objects;
        public AssetInfo[] assets;
        public ComponentInfo[] components;
        public PropertyInfo[] properties;
        public DiagnosticInfo[] diagnostics;
    }
    [Serializable] public sealed class SceneInfo { public string id, path, name; public bool active, dirty; }
    [Serializable] public sealed class ObjectInfo
    {
        public string id, globalId, name, parentId, sceneId, tag, prefabGuid;
        public int layer, siblingIndex;
        public bool activeSelf, activeInHierarchy;
        public float[] localPosition, worldPosition, localRotation, localScale;
        public ComponentInfo[] components;
    }
    [Serializable] public sealed class ComponentInfo { public string id, type, scriptGuid; public bool missing; }
    [Serializable] public sealed class AssetInfo { public string guid, path, type; public string[] labels, dependencies; public AssetReference[] references; }
    [Serializable] public sealed class AssetReference { public string id, name, type; }
    [Serializable] public sealed class PropertyInfo { public string path, type, value; public bool editable; }
    [Serializable] public sealed class DiagnosticInfo { public string severity, message, file, stack; public int line, column; }
}
