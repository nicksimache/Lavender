using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Lavender.Core.DataTypes;

namespace Lavender.Infrastructure.Backend
{
    public class FastApiService
    {
        private static FastApiService? _instance;
        private Process? _serverProcess;
        private FileStream? _ownershipLock;

        public static FastApiService Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new FastApiService();
                }

                return _instance;
            }
        }

        #region HTTPClient

        private readonly HttpClient httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        // Only the desktop owns the backend lifecycle. MCP callers use StartServerAsync
        // to connect to the existing server and must never restart it while indexing.
        public async Task StartFreshServerAsync(CancellationToken cancellationToken = default)
        {
            string projectRoot = FindProjectRoot();
            string stateDirectory = Path.Combine(projectRoot, "artifacts", "backend");
            Directory.CreateDirectory(stateDirectory);
            try
            {
                _ownershipLock = new FileStream(Path.Combine(stateDirectory, "owner.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Another Lavender instance is already managing this project's backend. Close it first.", ex);
            }

            try
            {
                // Match the exact Lavender launch command, not every Python process or
                // an arbitrary service listening on port 8000. Covers venv launcher + child.
                var cleanup = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                cleanup.Environment["LAVENDER_BACKEND_PYTHON"] = Path.Combine(projectRoot, ".venv", "Scripts", "python.exe");
                cleanup.ArgumentList.Add("-NoProfile");
                cleanup.ArgumentList.Add("-NonInteractive");
                cleanup.ArgumentList.Add("-Command");
                cleanup.ArgumentList.Add("$ErrorActionPreference = 'Stop'; " +
                    "$expected = '\"' + $env:LAVENDER_BACKEND_PYTHON + '\" -m uvicorn main:app --host 127.0.0.1 --port 8000'; " +
                    "$backendCandidates = @(Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | " +
                    "Where-Object { $_.CommandLine -and $_.CommandLine.Trim() -eq $expected }); " +
                    "foreach ($entry in $backendCandidates) { " +
                    "$current = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $entry.ProcessId); " +
                    "if ($current -and $current.CreationDate -eq $entry.CreationDate -and $current.CommandLine.Trim() -eq $expected) { " +
                    "Stop-Process -Id $entry.ProcessId -Force; Wait-Process -Id $entry.ProcessId -Timeout 5 -ErrorAction SilentlyContinue } }");
                using Process process = Process.Start(cleanup)
                    ?? throw new InvalidOperationException("Could not clean up the previous backend.");
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                await stdout;
                string error = await stderr;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Could not stop the previous Lavender backend: {error}");

                cancellationToken.ThrowIfCancellationRequested();
                await StartServerAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_serverProcess is null)
                    throw new InvalidOperationException("Port 8000 is already occupied. Lavender could not start a fresh backend.");
            }
            catch
            {
                StopServer();
                throw;
            }
        }

        public async Task StartServerAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsServerRunningAsync(cancellationToken))
            {
                return;
            }

            string projectRoot = FindProjectRoot();

            string pythonPath =
                Path.Combine(projectRoot, ".venv", "Scripts", "python.exe");

            string aiDirectory =
                Path.Combine(projectRoot, "backend", "app");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "-m uvicorn main:app --host 127.0.0.1 --port 8000",
                WorkingDirectory = aiDirectory,

                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _serverProcess = new Process
            {
                StartInfo = startInfo
            };

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Debug.WriteLine($"FastAPI: {e.Data}");
                }
            };

            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Debug.WriteLine($"FastAPI ERROR: {e.Data}");
                }
            };

            if (!_serverProcess.Start())
            {
                throw new InvalidOperationException("Could not start FastAPI.");
            }

            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            try { await WaitForServerAsync(cancellationToken); }
            catch { StopServer(); throw; }
        }

        private async Task<bool> IsServerRunningAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await httpClient.GetAsync("", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                string sourcePath = Path.Combine(FindProjectRoot(), "backend", "app", "main.py");
                string expectedRevision = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                using JsonDocument? health = await response.Content.ReadFromJsonAsync<JsonDocument>();
                if (health is null ||
                    !health.RootElement.TryGetProperty("service", out JsonElement service) ||
                    service.GetString() != "lavender" ||
                    !health.RootElement.TryGetProperty("source_revision", out JsonElement revision) ||
                    revision.GetString() != expectedRevision)
                {
                    throw new InvalidOperationException(
                        "An outdated or different backend is already running on port 8000. " +
                        "Close Lavender and stop its old Python backend, then launch Lavender again.");
                }

                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private async Task WaitForServerAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_serverProcess?.HasExited == true)
                {
                    throw new InvalidOperationException(
                        $"FastAPI exited during startup with code {_serverProcess.ExitCode}.");
                }

                if (await IsServerRunningAsync(cancellationToken))
                {
                    return;
                }

                await Task.Delay(500, cancellationToken);
            }

            throw new TimeoutException(
                "FastAPI did not start on localhost:8000.");
        }

        private static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lavender.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        public async Task EmbedProjectAsync(List<CodeChunk> chunks)
        {
            var request = new
            {
                chunks = chunks.Select(chunk => CodeChunk_ToPython.ToPythonChunk(chunk))
            };

            HttpResponseMessage response =
                await httpClient.PostAsJsonAsync(
                    "embed-project",
                    request);

            await EnsureSuccessWithBodyAsync(response);
        }

        public async Task<VectorSearchCodeChunk_ObjectRecv> SearchProjectAsync(string query, int topK)
        {
            var request = new
            {
                query,
                top_k = topK
            };

            HttpResponseMessage response =
                await httpClient.PostAsJsonAsync("search", request);

            await EnsureSuccessWithBodyAsync(response);

            VectorSearchCodeChunk_ObjectRecv? searchResponse =
                await response.Content.ReadFromJsonAsync<VectorSearchCodeChunk_ObjectRecv>();

            if (searchResponse == null)
            {
                throw new Exception("Search returned no data.");
            }

            return searchResponse;
        }

        private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string body = await response.Content.ReadAsStringAsync();
            string message = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase ?? "Request failed."
                : body;

            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). {message}");
        }

        public void StopServer()
        {
            Process? process = _serverProcess;
            _serverProcess = null;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited already or failed before it started.
            }
            finally
            {
                process?.Dispose();
                _ownershipLock?.Dispose();
                _ownershipLock = null;
            }
        }

        #endregion

    }
}
