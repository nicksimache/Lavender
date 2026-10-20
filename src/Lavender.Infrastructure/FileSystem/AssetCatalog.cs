using System.Windows.Media.Imaging;

namespace Lavender.Infrastructure.FileSystem;

public sealed record AssetEntry(string Path, string Format, long Bytes, DateTime ModifiedUtc);
public sealed record AssetPreview(string Path, int Width, int Height, int PreviewWidth, int PreviewHeight,
    long Bytes, int FrameCount, byte[] Png);

/// <summary>Local image inventory, independent of code embeddings and Roslyn.</summary>
public sealed class AssetCatalog(string projectRoot)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    public string RootPath => _root;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico"
    };

    public IReadOnlyList<AssetEntry> List(CancellationToken token = default)
    {
        List<AssetEntry> assets = [];
        Scan(_root);
        return assets.OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        void Scan(string directory)
        {
            token.ThrowIfCancellationRequested();
            string[] files;
            string[] folders;
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    return;
                files = Directory.GetFiles(directory);
                folders = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!Extensions.Contains(Path.GetExtension(file)))
                    continue;
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        assets.Add(new(Path.GetRelativePath(_root, file).Replace('\\', '/'),
                            info.Extension.TrimStart('.').ToLowerInvariant(), info.Length, info.LastWriteTimeUtc));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string folder in folders)
                if (!ProjectScanner.ShouldIgnoreFolder(folder))
                    Scan(folder);
        }
    }

    public AssetPreview Read(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || path.Contains(':'))
            throw new ArgumentException("Use a project-relative asset path.");
        string full = Path.GetFullPath(Path.Combine(_root, path));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Extensions.Contains(Path.GetExtension(full)))
            throw new ArgumentException("Choose a supported image inside the project.");
        // Reject junctions/symlinks in every component, including the project root.
        string current = _root;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Image reads through links are not supported.");
        foreach (string part in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Image reads through links are not supported.");
        }
        using var stream = File.OpenRead(full);
        if (stream.Length > 64 * 1024 * 1024)
            throw new IOException("Image exceeds the 64 MiB inspection limit.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
        int frames = decoder.Frames.Count;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (Math.Max(width, height) > 1024)
        {
            if (width >= height) image.DecodePixelWidth = 1024;
            else image.DecodePixelHeight = 1024;
        }
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new(Path.GetRelativePath(_root, full).Replace('\\', '/'), width, height,
            image.PixelWidth, image.PixelHeight, stream.Length, frames, output.ToArray());
    }
}
