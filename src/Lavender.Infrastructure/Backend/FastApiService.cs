using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Lavender.Core.DataTypes;

namespace Lavender.Infrastructure.Backend
{
    public class FastApiService
    {
        private static FastApiService? _instance;
        private Process? _serverProcess;

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

        private async Task<bool> IsServerRunning()
        {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync("");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task StartServerAsync()
        {
            if (await IsServerRunningAsync())
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

            await WaitForServerAsync();
        }

        private async Task<bool> IsServerRunningAsync()
        {
            try
            {
                using var response = await httpClient.GetAsync("");
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private async Task WaitForServerAsync()
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (_serverProcess?.HasExited == true)
                {
                    throw new InvalidOperationException(
                        $"FastAPI exited during startup with code {_serverProcess.ExitCode}.");
                }

                if (await IsServerRunningAsync())
                {
                    return;
                }

                await Task.Delay(500);
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

            response.EnsureSuccessStatusCode();
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

            response.EnsureSuccessStatusCode();

            VectorSearchCodeChunk_ObjectRecv? searchResponse =
                await response.Content.ReadFromJsonAsync<VectorSearchCodeChunk_ObjectRecv>();

            if (searchResponse == null)
            {
                throw new Exception("Search returned no data.");
            }

            return searchResponse;
        }

        public void StopServer()
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        #endregion

    }
}
