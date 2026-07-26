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

    public JsonConversationStore(string directory)
    {
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
        if (!File.Exists(path))
        {
            return null;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Conversation>(
                stream, _jsonOptions, cancellationToken);
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
        Directory.CreateDirectory(_directory);
        string path = GetPath(conversation.Id);
        string temporaryPath = path + ".tmp";

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
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
}
