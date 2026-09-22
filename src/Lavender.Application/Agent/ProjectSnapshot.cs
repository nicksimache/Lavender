using System.Security.Cryptography;
using System.Text;

namespace Lavender.Application.Agent;

public static class ProjectSnapshot
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".venv", "bin", "obj", "node_modules", "packages", "artifacts", "lavender_vectors", "__pycache__", "Library", "Temp", "Logs", "UserSettings" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".py", ".json", ".csproj", ".sln", ".md", ".txt", ".props", ".targets" };

    public static string? Compute(string? root)
    {
        if (root is null || !Directory.Exists(root)) return null;
        try
        {
            var entries = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                foreach (string child in Directory.EnumerateDirectories(dir))
                    if (!Ignored.Contains(Path.GetFileName(child)) &&
                        !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    if (!Extensions.Contains(Path.GetExtension(path))) continue;
                    var file = new FileInfo(path);
                    entries.Add($"{Path.GetRelativePath(root, path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
