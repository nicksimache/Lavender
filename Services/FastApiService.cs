using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Services
{
    internal class FastApiService
    {
        private static FastApiService? _instance;

        public static FastApiService Instance 
        { 
            get 
            { 
                if(_instance == null) _instance = new FastApiService();
                return _instance; 
            } 
        }

        /// <summary>
        /// New chunk class that gets sent to python api
        /// Made this so if we want to change codechunk in the future to include data relevant to WPF project - won't affect data sent to api
        /// </summary>
        private class PythonCodeChunk
        {
            public string id { get; set; } = "";
            public string file_path { get; set; } = "";
            public string chunk_type { get; set; } = "";
            public string @namespace { get; set; } = "";
            public string class_name { get; set; } = "";
            public string member_name { get; set; } = "";
            public string signature { get; set; } = "";
            public int start_line { get; set; } = 0;
            public int end_line { get; set; } = 0;
            public string code { get; set; } = "";
            public string embedding_text { get; set; } = "";

            public static PythonCodeChunk ToPythonChunk(Chunking.CodeChunk chunk)
            {
                return new PythonCodeChunk
                {
                    id = chunk.Id,
                    file_path = chunk.FilePath,
                    chunk_type = chunk.ChunkType.ToString(),
                    @namespace = chunk.Namespace,
                    class_name = chunk.ClassName,
                    member_name = chunk.MemberName,
                    signature = chunk.Signature,
                    start_line = chunk.StartLine,
                    end_line = chunk.EndLine,
                    code = chunk.Code,
                    embedding_text = chunk.EmbeddingText
                };

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
            if (await IsServerRunning())
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments =
                    "/C .\\.venv\\Scripts\\activate.bat && uvicorn main:app --port 8000",
                WorkingDirectory =
                    @"C:\Users\nicks\source\repos\Lavender\AI_Services",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            await WaitForServer();
        }

        private async Task WaitForServer()
        {
            while (!await IsServerRunning())
            {
                await Task.Delay(500);
            }
        }

        public async Task EmbedProjectAsync(List<Chunking.CodeChunk> chunks)
        {
            var request = new
            {
                chunks = chunks.Select(chunk => PythonCodeChunk.ToPythonChunk(chunk))
            };

            HttpResponseMessage response =
                await httpClient.PostAsJsonAsync(
                    "embed-project",
                    request);

            response.EnsureSuccessStatusCode();
        }

        #endregion

    }
}
