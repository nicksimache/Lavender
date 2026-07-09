using Lavender.Chunking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Services
{
    public class ProjectIndexer
    {
        private readonly CodeChunkService codeChunkService;
        private readonly FastApiService fastApiService;

        public ProjectIndexer()
        {
            codeChunkService = new CodeChunkService();
            fastApiService = FastApiService.Instance;
        }

        public async Task IndexProjectAsync(string projectPath)
        {
            List<CodeChunk> chunks =
                CodeChunkService.GetCodeChunksFromFolder(projectPath);

            await fastApiService.EmbedProjectAsync(chunks);
        }
    }
}
