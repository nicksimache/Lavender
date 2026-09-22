using System.Text.Json;

namespace Lavender.Infrastructure.FileSystem;

public sealed record LastProject(string ProjectPath, string SolutionPath);

public sealed class LastProjectStore
{
    private readonly string _path;

    public LastProjectStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lavender");
        _path = Path.Combine(directory, "last-project.json");
    }

    public async Task<LastProject?> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = File.OpenRead(_path);
            var project = await JsonSerializer.DeserializeAsync<LastProject>(stream, cancellationToken: token);
            return project is not null && Directory.Exists(project.ProjectPath) && File.Exists(project.SolutionPath)
                ? project : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(string projectPath, string solutionPath, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var project = new LastProject(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath)), Path.GetFullPath(solutionPath));
        string temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(project), token);
        File.Move(temporary, _path, overwrite: true);
    }
}
