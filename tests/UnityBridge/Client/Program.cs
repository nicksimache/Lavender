using Lavender.Infrastructure.Unity;
using Lavender.UnityBridge;
var project = Path.GetFullPath(args[0]);
var client = new UnityBridgeClient();
Console.WriteLine("Client: " + BridgeProtocol.PipeName(project) + "\n" + BridgeProtocol.CanonicalPath(project) + "\n" + Environment.UserName + ":" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
Console.WriteLine("Unity: " + File.ReadAllText(Path.Combine(project, "client-ready.txt")));
try
{
    var state = await client.ExecuteAsync(project, new() { action = "get_state" });
    Check(state.GetProperty("success").GetBoolean(), "Read real Unity state from .NET client");
    var create = new BridgeRequest { action = "create", session = state.GetProperty("session").GetString(),
        expectedRevision = state.GetProperty("revision").GetInt64(), requestId = Guid.NewGuid().ToString("N"),
        sceneId = state.GetProperty("activeSceneId").GetString(), name = "ClientTest", typeName = "Cube", position = [1, 2, 3] };
    var result = await client.ExecuteAsync(project, create);
    Check(result.GetProperty("success").GetBoolean(), "Create primitive with System.Text.Json request");
    var repeated = await client.ExecuteAsync(project, create);
    Check(repeated.GetProperty("success").GetBoolean(), "Retry acknowledged");
    var found = await client.ExecuteAsync(project, new() { action = "find_objects", query = "ClientTest" });
    Check(found.GetProperty("objects").GetArrayLength() == 1, "Retry did not duplicate scene object");
    var inspect = await client.ExecuteAsync(project, new() { action = "inspect_asset", assetGuid =
        (await client.ExecuteAsync(project, new() { action = "find_assets", query = "t:Prefab" })).GetProperty("assets")[0].GetProperty("guid").GetString() });
    Check(inspect.GetProperty("assets")[0].GetProperty("references").GetArrayLength() > 0, "Asset reference IDs cross the transport");
}
finally { File.WriteAllText(Path.Combine(project, "client-done.txt"), "done"); }
static void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS: " + text); }
