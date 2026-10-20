using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lavender.Infrastructure.AI;

/// <summary>Local request audit. Never records HTTP headers or credentials.</summary>
public static class ApiUsageTracker
{
    private static readonly AsyncLocal<object?> Context = new();
    private static readonly AsyncLocal<string?> CallId = new();
    private static readonly object Gate = new();
    public static string DirectoryPath => Environment.GetEnvironmentVariable("LAVENDER_API_LOG_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lavender", "ApiUsage");

    public static IDisposable Scope(object context)
    {
        object? previous = Context.Value;
        Context.Value = context;
        return new Restore(() => Context.Value = previous);
    }

    public static IDisposable BeginCall()
    {
        string? previous = CallId.Value;
        CallId.Value = Guid.NewGuid().ToString("N");
        return new Restore(() => CallId.Value = previous);
    }

    public static void Write(object data)
    {
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow, process_id = Environment.ProcessId,
                context = Context.Value, logical_call_id = CallId.Value, data
            });
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath,
                    $"desktop-{DateTime.UtcNow:yyyy-MM-dd}-{Environment.ProcessId}.jsonl"), line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Trace.TraceError($"API usage logging failed: {ex.Message}");
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>One start/end pair per HTTP attempt, including SDK retries.</summary>
public sealed class ApiTrackingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        string? payload = request.Content is null ? null :
            await request.Content.ReadAsStringAsync(cancellationToken);
        ApiUsageTracker.Write(new { event_type = "request", request_id = id,
            endpoint = request.RequestUri?.GetLeftPart(UriPartial.Path),
            method = request.Method.Method, request_body = payload });
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            JsonNode? usage = null;
            string? responseId = null;
            bool streaming = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (!streaming)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    JsonNode? root = JsonNode.Parse(body);
                    usage = root?["usage"]?.DeepClone();
                    responseId = root?["id"]?.GetValue<string>();
                }
                catch (JsonException) { }
            }
            ApiUsageTracker.Write(new { event_type = "response", request_id = id,
                status_code = (int)response.StatusCode, duration_ms = timer.Elapsed.TotalMilliseconds,
                provider_request_id = response.Headers.TryGetValues("x-request-id", out var values)
                    ? values.FirstOrDefault() : null,
                response_id = responseId, usage, streaming });
            return response;
        }
        catch (Exception ex)
        {
            ApiUsageTracker.Write(new { event_type = "error", request_id = id,
                duration_ms = timer.Elapsed.TotalMilliseconds, error_type = ex.GetType().Name });
            throw;
        }
    }
}
