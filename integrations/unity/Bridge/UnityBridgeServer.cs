#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lavender.UnityBridge
{
    [InitializeOnLoad]
    public static class UnityBridgeServer
    {
        private sealed class Pending
        {
            public string json;
            public CancellationToken token;
            public TaskCompletionSource<string> result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static readonly ConcurrentQueue<Pending> Queue = new ConcurrentQueue<Pending>();
        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private static readonly string Root;
        static UnityBridgeServer()
        {
            Root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            UnitySceneCommands.Initialize(Root);
            EditorApplication.update += Drain;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (Application.platform == RuntimePlatform.WindowsEditor)
                _ = Task.Run(Listen).ContinueWith(task => Debug.LogException(task.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }
        private static void Stop()
        {
            Lifetime.Cancel();
            EditorApplication.update -= Drain;
            while (Queue.TryDequeue(out var item)) item.result.TrySetCanceled();
        }
        private static void Drain()
        {
            // One command per editor update; all Unity APIs stay on the main thread.
            if (!Queue.TryDequeue(out var item)) return;
            if (item.token.IsCancellationRequested) { item.result.TrySetCanceled(); return; }
            try
            {
                var request = JsonUtility.FromJson<BridgeRequest>(item.json);
                item.result.TrySetResult(JsonUtility.ToJson(UnitySceneCommands.Execute(request)));
            }
            catch (Exception ex)
            {
                item.result.TrySetResult(JsonUtility.ToJson(new BridgeResponse { error = ex.Message }));
            }
        }
        private static async Task Listen()
        {
            while (!Lifetime.IsCancellationRequested)
            {
                try
                {
                    // Unity's Mono throws in WindowsIdentity.Owner for CurrentUserOnly.
                    // OWNER RIGHTS grants access only to the pipe's creator/owner; the
                    // protected DACL prevents inherited broad access without that API.
                    var security = new PipeSecurity();
                    security.SetSecurityDescriptorSddlForm("D:P(A;;GA;;;S-1-3-4)");
                    using (var pipe = new NamedPipeServerStream(BridgeProtocol.PipeName(Root), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
                    {
                        await pipe.WaitForConnectionAsync(Lifetime.Token).ConfigureAwait(false);
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(12));
                            var pending = new Pending { json = await BridgeProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false), token = timeout.Token };
                            Queue.Enqueue(pending);
                            using (timeout.Token.Register(() => pending.result.TrySetCanceled()))
                            {
                                string json = await pending.result.Task.ConfigureAwait(false);
                                await BridgeProtocol.WriteAsync(pipe, json, timeout.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (!Lifetime.IsCancellationRequested)
                        await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }
    }
}
#endif
