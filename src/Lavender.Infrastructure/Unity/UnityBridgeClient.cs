using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Lavender.UnityBridge;

namespace Lavender.Infrastructure.Unity;

public sealed class UnityBridgeClient
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public async Task<JsonElement> ExecuteAsync(string projectPath, BridgeRequest request, CancellationToken token = default)
    {
        request.projectPath = Path.GetFullPath(projectPath);
        request.version = BridgeProtocol.Version;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", BridgeProtocol.PipeName(projectPath), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
            await BridgeProtocol.WriteAsync(pipe, JsonSerializer.Serialize(request, Json), timeout.Token).ConfigureAwait(false);
            string json = await BridgeProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            using var response = JsonDocument.Parse(json);
            return response.RootElement.Clone();
        }
        catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Unity bridge is disconnected or busy. Open the matching Unity project with LavenderBridge installed. " +
                "If a write timed out, retry with the SAME requestId, session and arguments to avoid duplicates; after a session change inspect the scene before retrying.", ex);
        }
    }
}
