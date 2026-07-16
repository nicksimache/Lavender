using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Backend;
using Lavender.Infrastructure.Chunking;

namespace Lavender.Infrastructure.Indexing
{
    public class ProjectIndexer
    {
        private readonly FastApiService fastApiService;

        public ProjectIndexer()
        {
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
