using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lavender.App.Rendering;

internal static class ImageAssets
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico"
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public static (ImageSource Image, string Description) Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        // Bound preview memory for large sprite sheets; the original file is never modified.
        if (Math.Max(width, height) > 4096)
        {
            if (width >= height) image.DecodePixelWidth = 4096;
            else image.DecodePixelHeight = 4096;
        }
        image.EndInit();
        image.Freeze();
        string description = $"{width:N0} × {height:N0} px  ·  {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}  ·  {stream.Length / 1024d:N1} KB";
        if (decoder.Frames.Count > 1)
            description += "  ·  First frame";
        return (image, description);
    }
}
