using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Backend;
using Lavender.Infrastructure.Indexing.Chunking;
using Lavender.Infrastructure.Indexing.Symbol;

namespace Lavender.Infrastructure.Indexing
{
    public class ProjectIndexer
    {
        private readonly FastApiService fastApiService;

        public ProjectIndexer()
        {
            fastApiService = FastApiService.Instance;
        }

        public async Task IndexProjectAsync(string projectPath, string solutionPath)
        {
            List<CodeChunk> chunks =
                CodeChunkService.GetCodeChunksFromFolder(projectPath);

            List<CodeSymbol> symbols =
                await SymbolIndexingService.IndexProjectAsync(solutionPath);

            await fastApiService.EmbedProjectAsync(chunks);
        }
    }
}
