using Lavender.Application.Agent;
using Lavender.Infrastructure.AI;
using Lavender.Infrastructure.Mcp;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

const string keyName = "LAVENDER_RESPONSES_PROBE_KEY";
Environment.SetEnvironmentVariable(keyName, "fixture-key");
string logDirectory = Path.Combine(Path.GetTempPath(), "lavender-api-probe-" + Guid.NewGuid().ToString("N"));
string? previousLogDirectory = Environment.GetEnvironmentVariable("LAVENDER_API_LOG_DIRECTORY");
Environment.SetEnvironmentVariable("LAVENDER_API_LOG_DIRECTORY", logDirectory);
try
{
    using var tracking = ApiUsageTracker.Scope(new { trigger = "fixture", tool = "read_file" });
    FixtureHandler handler = new();
    handler.Replies.Enqueue("""
        {"usage":{"input_tokens":12,"output_tokens":3,"total_tokens":15},"status":"completed","output":[
          {"id":"rs_1","type":"reasoning","encrypted_content":"opaque-reasoning"},
          {"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"a.cs\"}"}
        ]}
        """);
    handler.Replies.Enqueue("""
        {"status":"completed","output":[
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done."}]}
        ]}
        """);
    using HttpClient http = new(new ApiTrackingHandler(handler));
    ResponsesApiClient client = new(http);
    AgentModelOption model = new()
    {
        Id = "gpt-6-astra",
        Label = "GPT-6 Astra",
        ApiKeyEnvironmentVariable = keyName,
        ReasoningEffort = ReasoningEffort.XHigh
    };
    List<JsonNode> input = [ResponsesApiClient.Message("user", "Read a.cs")];
    McpToolDefinition[] tools =
        [new("read_file", "Read a source file", BinaryData.FromString("""{"type":"object","properties":{"path":{"type":"string"}}}"""))];

    ResponseTurn first = await client.CompleteAsync(model, input, tools, "You are a coding assistant.");
    Check(first.ToolCalls.Count == 1 &&
          first.ToolCalls[0] == new ResponseToolCall("call_1", "read_file", """{"path":"a.cs"}"""),
        "function call parsed");
    input.AddRange(first.OutputItems);
    input.Add(ResponsesApiClient.FunctionOutput("call_1", """{"content":"class A {}"}"""));
    ResponseTurn second = await client.CompleteAsync(model, input, [], "Summarize the result.");
    Check(second.Text == "Done.", "final output text parsed");

    JsonNode firstRequest = JsonNode.Parse(handler.Requests[0])!;
    JsonNode secondRequest = JsonNode.Parse(handler.Requests[1])!;
    Check(firstRequest["store"]!.GetValue<bool>() == false &&
          firstRequest["reasoning"]!["effort"]!.GetValue<string>() == "xhigh" &&
          firstRequest["tools"]![0]!["type"]!.GetValue<string>() == "function" &&
          firstRequest["tools"]![0]!["parameters"]!["type"]!.GetValue<string>() == "object",
        "stateless request carries selected reasoning and function schema");
    Check(secondRequest["input"]!.AsArray().Count == 4 &&
          secondRequest["input"]![1]!["encrypted_content"]!.GetValue<string>() == "opaque-reasoning" &&
          secondRequest["input"]![3]!["type"]!.GetValue<string>() == "function_call_output" &&
          secondRequest["tools"] is null,
        "reasoning item and tool result replayed without further tools");
    string audit = string.Join("\n", Directory.GetFiles(logDirectory).Select(File.ReadAllText));
    var events = audit.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
    Check(events.Length == 4, "one start/end pair per request");
    Check(events[0]["data"]!["request_body"]!.GetValue<string>() == handler.Requests[0], "exact wire payload recorded");
    Check(events[1]["data"]!["usage"]!["total_tokens"]!.GetValue<int>() == 15, "provider usage preserved");
    Check(events[0]["logical_call_id"]!.ToString() == events[1]["logical_call_id"]!.ToString(), "request correlation preserved");
    Check(events[0]["context"]!["tool"]!.GetValue<string>() == "read_file" && !audit.Contains("fixture-key"), "trigger recorded without credentials");
    Console.WriteLine("Responses API fixture: reasoning, tools, stateless replay, and final text passed.");
}
finally
{
    Environment.SetEnvironmentVariable(keyName, null);
    Environment.SetEnvironmentVariable("LAVENDER_API_LOG_DIRECTORY", previousLogDirectory);
}

if (args.Contains("--live", StringComparer.Ordinal))
{
    ResponsesApiClient liveClient = new();
    foreach (string modelId in new[] { "gpt-6-sol", "gpt-6-luna", "gpt-6-astra", "gpt-6.1-sol" })
    {
        AgentModelOption liveModel = new()
        {
            Id = modelId,
            Label = modelId,
            ReasoningEffort = ReasoningEffort.Low
        };
        List<JsonNode> liveInput =
            [ResponsesApiClient.Message("user", "Call lookup_number for pi, then report its value.")];
        McpToolDefinition[] liveTools =
            [new("lookup_number", "Look up a named number",
                BinaryData.FromString("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""))];
        ResponseTurn call = await liveClient.CompleteAsync(liveModel, liveInput, liveTools,
            "Use lookup_number to answer this request.");
        Check(call.ToolCalls.Count == 1 && call.ToolCalls[0].Name == "lookup_number",
            $"live {modelId} reasoning produced a function call");
        liveInput.AddRange(call.OutputItems);
        liveInput.Add(ResponsesApiClient.FunctionOutput(call.ToolCalls[0].CallId, "3.14"));
        ResponseTurn final = await liveClient.CompleteAsync(liveModel, liveInput, [],
            "Answer briefly using the tool result.");
        Check(final.Text.Contains("3.14", StringComparison.Ordinal),
            $"live {modelId} completed the tool loop");
        Console.WriteLine($"Live {modelId} Low: function call and final answer passed.");
    }
}

static void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception("FAIL: " + name);
}

sealed class FixtureHandler : HttpMessageHandler
{
    public Queue<string> Replies { get; } = new();
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (request.RequestUri?.AbsoluteUri != "https://api.openai.com/v1/responses" ||
            request.Headers.Authorization?.Scheme != "Bearer")
            throw new Exception("Unexpected Responses endpoint or authentication.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Replies.Dequeue())
        };
    }
}
