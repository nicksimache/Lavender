using System.Text;

namespace Lavender.Infrastructure.FileSystem;

/// <summary>Creates text files without exposing a partially written destination.</summary>
public static class ProjectFileCreator
{
    public static async Task<string> CreateAsync(string projectRoot, string path, string content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        if (Path.IsPathRooted(path)) throw new ArgumentException("Use a path relative to the project root.");
        string[] parts = path.Replace('\\', '/').Split('/');
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The path contains an invalid or protected component.");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Windows device names cannot be used as file names.");
        }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected project folder does not exist.");
        string destination = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        string relative = Path.GetRelativePath(root, destination);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("The file must stay inside the selected project.");
        void CheckDirectories()
        {
            string current = root;
            RejectLink(current);
            foreach (string part in parts[..^1])
            {
                current = Path.Combine(current, part);
                RejectLink(current);
            }
        }
        CheckDirectories();
        RejectLink(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The destination already exists.");
        string parent = Path.GetDirectoryName(destination)!;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(parent);
            CheckDirectories();
            string candidate = Path.Combine(parent, $".lavender-create-{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                temporary = candidate;
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            CheckDirectories();
            RejectLink(destination);
            File.Move(temporary, destination, overwrite: false);
            temporary = null;
            return relative.Replace('\\', '/');
        }
        finally
        {
            if (temporary is not null)
            {
                try { CheckDirectories(); File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void RejectLink(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Creating files through symbolic links or junctions is not supported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
