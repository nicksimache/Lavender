using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Lavender.App;

/// <summary>One editor per Windows user/session; only that user can send navigation requests.</summary>
internal sealed class ExternalOpenChannel : IDisposable
{
    private static readonly string Name = $"Lavender.Open.{WindowsIdentity.GetCurrent().User!.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private readonly Mutex _mutex = new(false, @"Local\" + Name);
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsMutex;

    public bool TryOwn()
    {
        try {
            return _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException) {
            return _ownsMutex = true;
        }
    }

    public static async Task SendAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(args).AsMemory(), timeout.Token);

        string? reply = await reader.ReadLineAsync(timeout.Token);

        if (reply != "OK")
        {
            throw new IOException(reply ?? "Lavender closed before accepting the request.");
        }
    }

    public async Task ListenAsync(Action<ExternalOpenRequest?> receive)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_stop.Token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                try
                {
                    string payload = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Empty request.");
                    receive(ExternalOpenRequest.Parse(JsonSerializer.Deserialize<string[]>(payload)
                        ?? throw new ArgumentException("Invalid navigation request.")));
                    await writer.WriteLineAsync("OK".AsMemory(), timeout.Token);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or UnauthorizedAccessException)
                {
                    await writer.WriteLineAsync(ex.Message.AsMemory(), timeout.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { /* A disconnected client must not stop future navigation. */ }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
