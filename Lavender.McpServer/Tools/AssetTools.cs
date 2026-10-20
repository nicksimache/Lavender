using Lavender.Infrastructure.FileSystem;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class AssetTools(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_list_assets")]
    [Description("Lists project image assets without embeddings or vision API calls. Searches relative paths; returns paths, formats, byte sizes and modification times. Refreshed from disk on each call, including while code indexing runs. Use lavender_inspect_image to see pixels and dimensions.")]
    public object ListAssets(string query = "", int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var catalog = RequireCatalog();
        var all = catalog.List(cancellationToken);
        var matches = all
            .Where(asset => asset.Path.Contains(query ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        string message = all.Count == 0
            ? $"No supported images found under {catalog.RootPath}. Only this project folder is searched, not sibling projects or Downloads. Ask the user to add assets here or open the folder containing them; do not substitute a different game format without asking."
            : $"Found {matches.Length} matching image assets out of {all.Count} under {catalog.RootPath}.";
        return new { success = true, message, project_root = catalog.RootPath,
            catalog_total = all.Count, total = matches.Length, offset, assets = matches.Skip(offset).Take(limit),
            has_more = (long)offset + limit < matches.Length };
    }

    [McpServerTool(Name = "lavender_inspect_image")]
    [Description("Visually inspects a project-relative image asset. Returns actual pixels plus original dimensions and path, allowing selection of sprites/tiles and correct code references. PNG/JPEG/BMP/GIF/TIFF/ICO supported. Preview is at most 1024 pixels on its longest edge; first frame only. Does not modify files.")]
    public CallToolResult InspectImage(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            AssetPreview preview = RequireCatalog().Read(path, cancellationToken);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = JsonSerializer.Serialize(new {
                        success = true, path = preview.Path, width = preview.Width, height = preview.Height,
                        preview_width = preview.PreviewWidth, preview_height = preview.PreviewHeight,
                        bytes = preview.Bytes, frame_count = preview.FrameCount, displayed_frame = 0 }) },
                    ImageContentBlock.FromBytes(preview.Png, "image/png")
                ]
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CallToolResult { IsError = true,
                Content = [new TextContentBlock { Text = $"Could not inspect image: {ex.Message}" }] };
        }
    }

    private AssetCatalog RequireCatalog() => state.Assets
        ?? throw new InvalidOperationException("Open a project first.");
}
