namespace Lavender.Infrastructure.FileSystem;

/// <summary>Source and asset discovery without embedding or opening binary files.</summary>
public static class ProjectFileCatalog
{
    public static IEnumerable<string> Enumerate(string root, CancellationToken token = default)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
            // Surface access failures instead of claiming that a partial inventory is complete.
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) yield return file;
            }
            foreach (string folder in Directory.EnumerateDirectories(directory))
                if (!ProjectScanner.ShouldIgnoreFolder(folder)) pending.Push(folder);
        }
    }
}
