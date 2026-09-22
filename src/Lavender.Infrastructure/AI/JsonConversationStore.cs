using Lavender.Application.Agent;
using System.Text.Json;

namespace Lavender.Infrastructure.AI;

public sealed class JsonConversationStore : IConversationStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Dictionary<Guid, Conversation> _memory = new();
    private readonly bool _persist;

    public JsonConversationStore(string directory, bool persist = true)
    {
        _persist = persist;
        _directory = Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);
    }

    public async Task<Conversation> CreateAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Conversation conversation = new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await SaveAsync(conversation, cancellationToken);
        return conversation;
    }

    public async Task<Conversation?> LoadAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(id);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (_memory.TryGetValue(id, out Conversation? cached)) return cached;
            if (!_persist || !File.Exists(path)) return null;
            await using FileStream stream = File.OpenRead(path);
            Conversation? loaded = await JsonSerializer.DeserializeAsync<Conversation>(
                stream, _jsonOptions, cancellationToken);
            if (loaded is not null) _memory[id] = loaded;
            return loaded;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(conversation.Id);
        string temporaryPath = path + ".tmp";

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            conversation.ProjectPath = NormalizeProjectPath(conversation.ProjectPath);
            _memory[conversation.Id] = conversation;
            if (!_persist) return;
            Directory.CreateDirectory(_directory);
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, conversation, _jsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string GetPath(Guid id) =>
        Path.Combine(_directory, $"{id:N}.json");

    public async Task<IReadOnlyList<Conversation>> ListAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        projectPath = NormalizeProjectPath(projectPath);
        if (_persist && Directory.Exists(_directory))
        {
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid id)) continue;
                try { await LoadAsync(id, cancellationToken); }
                catch (JsonException) { /* One damaged history must not hide the others. */ }
                catch (IOException) { }
            }
        }
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            return _memory.Values.Where(c => string.Equals(NormalizeProjectPath(c.ProjectPath), projectPath,
                StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.UpdatedAt).ToArray();
        }
        finally { _fileLock.Release(); }
    }

    public static string? NormalizeProjectPath(string? path) => string.IsNullOrWhiteSpace(path)
        ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public async Task ClearProjectAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        var chats = await ListAsync(projectPath, cancellationToken);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            foreach (Conversation chat in chats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_persist)
                {
                    File.Delete(GetPath(chat.Id));
                    File.Delete(GetPath(chat.Id) + ".tmp");
                }
                _memory.Remove(chat.Id);
            }
        }
        finally { _fileLock.Release(); }
    }
}
