using Lavender.App;
using System.Diagnostics;

if (args.FirstOrDefault() == "--client")
{
    using var contender = new ExternalOpenChannel();
    if (contender.TryOwn()) throw new Exception("Second process incorrectly became the owner.");
    ExternalOpenChannel.SendAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
    return;
}

string folder = Path.Combine(Path.GetTempPath(), "Lavender navigation " + Guid.NewGuid());
Directory.CreateDirectory(folder);
string file = Path.Combine(folder, "Player script.cs");
File.WriteAllText(file, "class Player {}\n");
try
{
    var parsed = ExternalOpenRequest.Parse(["--project", folder, "--file", "Player script.cs", "--line", "42", "--column", "8"]);
    Check(parsed == new ExternalOpenRequest(file, 42, 8, folder), "Paths with spaces and relative paths");
    Check(ExternalOpenRequest.Parse([]) is null, "Empty launch activates existing window");
    Check(ExternalOpenRequest.Parse(["--file", file])?.Line == 1, "Default location");
    Reject<ArgumentException>(["--file"]);
    Reject<ArgumentException>(["--file", file, "--line", "0"]);
    Reject<ArgumentException>(["--file", file, "--column", "wrong"]);
    Reject<ArgumentException>(["--unknown", file]);
    Reject<FileNotFoundException>(["--file", Path.Combine(folder, "missing.cs")]);

    using var owner = new ExternalOpenChannel();
    Check(owner.TryOwn(), "Own navigation channel (close Lavender before running this test)");
    var received = new System.Collections.Concurrent.ConcurrentQueue<ExternalOpenRequest?>();
    Task listener = owner.ListenAsync(received.Enqueue);
    for (int i = 1; i <= 3; i++)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--client", "--file", file, "--line", i.ToString() }) start.ArgumentList.Add(argument);
        using var client = Process.Start(start)!;
        if (!client.WaitForExit(30000)) { client.Kill(); throw new Exception("Forwarding client timed out."); }
        Check(client.ExitCode == 0, "Second process forwards and exits: " + client.StandardOutput.ReadToEnd() + client.StandardError.ReadToEnd());
        Check(received.TryDequeue(out var request) && request?.Line == i && request.FilePath == file, "Delivered exact source location");
    }
    try
    {
        ExternalOpenChannel.SendAsync(["--file", file, "--line", "bad"]).GetAwaiter().GetResult();
        throw new Exception("Invalid forwarded request was accepted.");
    }
    catch (IOException) { }
    ExternalOpenChannel.SendAsync([]).GetAwaiter().GetResult();
    Check(received.TryDequeue(out var activation) && activation is null, "Listener survives invalid request");
    // Keep ownership/disposal on this thread because Windows mutex ownership is thread-affine.
    Console.WriteLine("All external navigation checks passed.");
}
finally
{
    File.Delete(file);
    Directory.Delete(folder);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
static void Reject<T>(string[] arguments) where T : Exception
{
    try { ExternalOpenRequest.Parse(arguments); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
