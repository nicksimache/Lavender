namespace Lavender.Infrastructure.FileSystem;

/// <summary>Rejects Lavender's own context notices, which are never executable source.</summary>
public static class SourceWriteGuard
{
    public static void Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (string marker in new[] { "[Historical edit body omitted;", "[Tool result truncated by Lavender.]",
            "Source omitted to bound working context;", "Earlier read content omitted to reduce context." })
            if (content.Contains(marker, StringComparison.Ordinal))
                throw new ArgumentException("Write rejected: content contains a Lavender context/truncation notice. Read the current source and supply actual code. No file was changed.");
    }
}
