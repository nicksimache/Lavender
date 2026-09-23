using System.Diagnostics;
using System.Text.Json;

namespace Lavender.Infrastructure.Indexing;

public sealed class IndexingTimings
{
    public static string ReportDirectory => Environment.GetEnvironmentVariable("LAVENDER_INDEX_REPORT_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lavender", "Diagnostics", "Indexing");
    private readonly Stopwatch _total = Stopwatch.StartNew();
    public string ProjectPath { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public Dictionary<string, double> StageMilliseconds { get; } = new();
    public int ChunkCount { get; set; }
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public JsonElement? Backend { get; set; }
    public string? Error { get; set; }
    public string Status { get; private set; } = "Running";
    public double TotalMilliseconds { get; private set; }
    public string? LoggingError { get; private set; }
    public string ReportPath { get; } = Path.Combine(
        ReportDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");

    public IndexingTimings(string projectPath)
    {
        ProjectPath = projectPath;
        Save();
    }

    public IDisposable Stage(string name) => new Measurement(this, name);

    public void Finish()
    {
        _total.Stop();
        Status = Error is null ? "Completed" : "Failed";
        TotalMilliseconds = _total.Elapsed.TotalMilliseconds;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LoggingError = ex.Message; }
    }

    public string Summary() => $"Indexing {Status.ToLowerInvariant()} in {TotalMilliseconds / 1000:F2}s; " +
        $"{FileCount} files, {ChunkCount} chunks, {SymbolCount} symbols.\n" +
        string.Join("\n", StageMilliseconds.Select(s => $"{s.Key}: {s.Value / 1000:F2}s")) +
        (Backend is { } backend ? $"\nBackend breakdown (included in embed HTTP time): {backend}" : "") +
        (LoggingError is null ? $"\nTiming report: {ReportPath}" : $"\nCould not save timing report: {LoggingError}");

    private sealed class Measurement(IndexingTimings owner, string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public void Dispose()
        {
            _watch.Stop();
            owner.StageMilliseconds[name] = _watch.Elapsed.TotalMilliseconds;
            owner.TotalMilliseconds = owner._total.Elapsed.TotalMilliseconds;
            owner.Save();
        }
    }
}
