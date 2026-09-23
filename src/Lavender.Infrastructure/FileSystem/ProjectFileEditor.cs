using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lavender.Infrastructure.FileSystem;

public sealed record ProjectFileRead(string Path, string ContentHash, int LineCount, string Encoding, string Content);
public sealed record ProjectFileEdit(string Path, string? DestinationPath, string? ContentHash, string? BackupPath, bool Changed);

/// <summary>Project-scoped edits guarded by the hash returned by ReadAsync.</summary>
public sealed class ProjectFileEditor(string projectRoot, string? backupDirectory = null)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    private const int MaxBytes = 4 * 1024 * 1024;

    private string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path)) throw new ArgumentException("Use a project-relative file path.");
        string[] parts = path.Replace('\\', '/').Split('/');
        foreach (string part in parts)
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Invalid or protected file path.");
        }
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Project folder does not exist.");
        string current = _root;
        CheckLink(current);
        foreach (string part in parts) { current = Path.Combine(current, part); CheckLink(current); }
        return current;
    }

    private static void CheckLink(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("File operations through symbolic links or junctions are not supported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private async Task<byte[]> BytesAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string full = Resolve(path);
        if (!File.Exists(full)) throw new FileNotFoundException("File does not exist.", path);
        if (new FileInfo(full).Length > MaxBytes) throw new IOException("File exceeds the 4 MiB tool limit.");
        byte[] bytes = await File.ReadAllBytesAsync(full, token);
        if (bytes.Length > MaxBytes) throw new IOException("File exceeds the 4 MiB tool limit.");
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Verify(byte[] bytes, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File contents changed or the hash is missing. Call lavender_read_file and use its content_hash before retrying.");
    }

    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw new IOException("UTF-32 files are not supported for text editing.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new IOException("Binary files are not supported for text editing.");
        return (text, encoding, bytes[..skip]);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int>();
        if (text.Length == 0) return starts;
        starts.Add(0);
        foreach (Match match in Regex.Matches(text, "\\r\\n|\\r|\\n"))
            if (match.Index + match.Length < text.Length) starts.Add(match.Index + match.Length);
        return starts;
    }

    public async Task<ProjectFileRead> ReadAsync(string path, CancellationToken token = default)
    {
        byte[] bytes = await BytesAsync(path, token);
        var decoded = Decode(bytes);
        return new(path.Replace('\\', '/'), Hash(bytes), LineStarts(decoded.Text).Count, decoded.Encoding.WebName, decoded.Text);
    }

    private async Task<string> BackupAsync(string path, byte[] bytes, string operation, string? destination, CancellationToken token)
    {
        string rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
        string directory = Path.Combine(backupDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lavender", "Backups"), rootKey, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory, "original.bin");
        await File.WriteAllBytesAsync(backup, bytes, token);
        await File.WriteAllTextAsync(Path.Combine(directory, "operation.json"), JsonSerializer.Serialize(new
        { ProjectRoot = _root, Path = path, Destination = destination, Operation = operation, OriginalHash = Hash(bytes), CreatedAt = DateTimeOffset.UtcNow }), token);
        return backup;
    }

    public async Task<ProjectFileEdit> DeleteAsync(string path, string expectedHash, CancellationToken token = default)
    {
        byte[] bytes = await BytesAsync(path, token);
        Verify(bytes, expectedHash);
        string backup = await BackupAsync(path, bytes, "delete", null, token);
        Verify(await BytesAsync(path, token), expectedHash);
        token.ThrowIfCancellationRequested();
        File.Delete(Resolve(path));
        return new(path, null, null, backup, true);
    }

    public async Task<ProjectFileEdit> MoveAsync(string path, string destination, string expectedHash, CancellationToken token = default)
    {
        byte[] bytes = await BytesAsync(path, token);
        Verify(bytes, expectedHash);
        string target = Resolve(destination);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Destination already exists. Move never overwrites.");
        string backup = await BackupAsync(path, bytes, "move", destination, token);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Verify(await BytesAsync(path, token), expectedHash);
        token.ThrowIfCancellationRequested();
        File.Move(Resolve(path), Resolve(destination), overwrite: false);
        return new(path, destination, Hash(bytes), backup, true);
    }

    public async Task<ProjectFileEdit> WriteLinesAsync(string path, int startLine, int endLine, string replacement,
        string expectedHash, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        byte[] bytes = await BytesAsync(path, token);
        Verify(bytes, expectedHash);
        var (text, encoding, preamble) = Decode(bytes);
        var starts = LineStarts(text);
        bool insertion = endLine == startLine - 1;
        if (startLine < 1 || startLine > starts.Count + (insertion ? 1 : 0) || endLine < startLine - 1 || endLine > starts.Count)
            throw new ArgumentException($"Invalid range. File has {starts.Count} lines. Use inclusive one-based lines, or endLine = startLine - 1 to insert.");
        int from = startLine <= starts.Count ? starts[startLine - 1] : text.Length;
        int to = insertion ? from : endLine < starts.Count ? starts[endLine] : text.Length;
        string newline = Regex.Match(text, "\\r\\n|\\r|\\n") is { Success: true } match ? match.Value : "\n";
        string added = replacement.ReplaceLineEndings(newline);
        if (added.Length > 0)
        {
            bool needsEnding = to < text.Length || (!insertion && to > from && (text[to - 1] is '\n' or '\r'))
                || (insertion && from == text.Length && from > 0 && (text[^1] is '\n' or '\r'));
            if (needsEnding && !(added.EndsWith('\n') || added.EndsWith('\r'))) added += newline;
            if (insertion && from == text.Length && from > 0 && !(text[^1] is '\n' or '\r')) added = newline + added;
        }
        string updated = text[..from] + added + text[to..];
        if (updated == text) return new(path, null, Hash(bytes), null, false);
        byte[] updatedBytes = preamble.Concat(encoding.GetBytes(updated)).ToArray();
        if (updatedBytes.Length > MaxBytes) throw new IOException("Result exceeds the 4 MiB tool limit.");
        string backup = await BackupAsync(path, bytes, "write_lines", null, token);
        string target = Resolve(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".lavender-edit-{Guid.NewGuid():N}.tmp");
        bool owned = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                owned = true;
                await stream.WriteAsync(updatedBytes, token);
                await stream.FlushAsync(token);
            }
            Verify(await BytesAsync(path, token), expectedHash);
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, Resolve(path), destinationBackupFileName: null);
            owned = false;
            return new(path, null, Hash(updatedBytes), backup, true);
        }
        finally
        {
            if (owned)
            {
                try { Resolve(path); File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
