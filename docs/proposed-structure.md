# Lavender Proposed Project Structure

Lavender is currently a WPF app with a Python vector-search backend. The next clean step is to split the system by responsibility instead of by "whatever service exists today". The important boundaries are:

- UI: WPF views, view models, rendering, commands.
- Application: user workflows like "index project", "ask question", "open file", "retrieve context".
- Core: project/code models and interfaces that describe what Lavender can do.
- Infrastructure: concrete file system, OpenAI, FastAPI, LanceDB, process, persistence, and Roslyn implementations.
- AI backend: Python service for embeddings/vector stores, later expandable to more indexes.
- Unity: future Unity editor bridge and Unity-specific context/tools.

## Recommended Layout

```text
Lavender.sln

src/
  Lavender.App/
    App.xaml
    App.xaml.cs
    MainWindow.xaml
    MainWindow.xaml.cs

    Views/
      ChatView.xaml
      ProjectExplorerView.xaml
      ContextSourcesView.xaml
      CodePreviewView.xaml
      SettingsView.xaml

    ViewModels/
      MainViewModel.cs
      ChatViewModel.cs
      ProjectExplorerViewModel.cs
      ContextSourcesViewModel.cs
      CodePreviewViewModel.cs
      SettingsViewModel.cs

    Commands/
      RelayCommand.cs
      AsyncRelayCommand.cs

    Rendering/
      SyntaxHighlighter.cs
      RichTextBoxRenderer.cs

    Converters/
      HeaderToImageConverter.cs

    Assets/
      Images/
        cs_icon.png
        C_Sharp_Logo_2023.png

    DependencyInjection/
      ServiceRegistration.cs

  Lavender.Application/
    Chat/
      AskQuestionHandler.cs
      ConversationService.cs
      PromptBuilder.cs
      ContextAssembler.cs

    Projects/
      OpenProjectHandler.cs
      IndexProjectHandler.cs
      RefreshProjectIndexHandler.cs

    Retrieval/
      ContextRetrievalService.cs
      RetrievalPipeline.cs
      ContextBudgetManager.cs
      ContextRanker.cs

  Lavender.Core/
    Projects/
      ProjectContext.cs
      ProjectFile.cs
      ProjectScannerOptions.cs
      IProjectScanner.cs

    Files/
      IFileReader.cs
      IFileHasher.cs
      FileSnapshot.cs

    Chunking/
      CodeChunk.cs
      ChunkLocation.cs
      ChunkMetadata.cs
      ChunkType.cs
      ICodeChunker.cs
      IChunkStrategy.cs
      IEmbeddingTextBuilder.cs

    Indexing/
      IndexingRequest.cs
      IndexingResult.cs
      IndexedRecord.cs
      IndexKind.cs
      IProjectIndexer.cs
      IIndexStore.cs
      IIndexStateStore.cs

    Retrieval/
      RetrievalRequest.cs
      RetrievalResult.cs
      RetrievedContext.cs
      ContextSource.cs
      ContextSourceKind.cs
      IContextRetriever.cs
      IContextStore.cs

    AI/
      ChatRequest.cs
      ChatResponse.cs
      AgentMessage.cs
      IAIClient.cs
      IPromptBuilder.cs
      IContextAssembler.cs

    Common/
      Result.cs
      Error.cs
      PathUtility.cs
      HashUtility.cs

  Lavender.Infrastructure/
    FileSystem/
      ProjectScanner.cs
      FileReader.cs
      FileHasher.cs
      IgnoreRules.cs

    Chunking/
      RoslynCodeChunker.cs
      EmbeddingTextBuilder.cs
      Strategies/
        WholeFileChunkStrategy.cs
        FileSummaryChunkStrategy.cs
        ClassFieldsChunkStrategy.cs
        MethodChunkStrategy.cs
        ConstructorChunkStrategy.cs
        PropertyChunkStrategy.cs
        TypeDeclarationChunkStrategy.cs

    CodeAnalysis/
      RoslynWorkspaceFactory.cs
      RoslynSymbolIndexBuilder.cs
      RoslynReferenceIndexBuilder.cs

    Indexing/
      ProjectIndexer.cs
      IncrementalProjectIndexer.cs
      FileHashChangeDetector.cs
      JsonIndexStateStore.cs

    Retrieval/
      SemanticContextRetriever.cs
      KeywordContextRetriever.cs
      SymbolContextRetriever.cs
      OpenFileContextRetriever.cs

    AI/
      OpenAIChatClient.cs

    Backend/
      FastApiClient.cs
      FastApiProcessManager.cs
      Requests/
        EmbedRecordsRequest.cs
        SearchRequest.cs
      Responses/
        SearchResponse.cs

    Persistence/
      SettingsStore.cs
      ConversationStore.cs

  Lavender.Unity/
    UnityProjectContext.cs
    UnitySceneContext.cs
    UnityAssetContext.cs
    IUnityConnection.cs
    IUnityContextProvider.cs
    IUnityCommandService.cs
    Retrieval/
      UnitySceneContextRetriever.cs
      UnitySelectionContextRetriever.cs
      UnityConsoleContextRetriever.cs
    Tools/
      GetCurrentSceneTool.cs
      GetSelectedObjectTool.cs
      CreateGameObjectTool.cs
      AddComponentTool.cs

  Lavender.Tests/
    Chunking/
    Indexing/
    Retrieval/
    Chat/
    Infrastructure/

backend/
  app/
    main.py

    api/
      health_routes.py
      embeddings_routes.py
      search_routes.py
      indexes_routes.py

    models/
      index_record.py
      embedding_request.py
      search_request.py
      search_result.py

    services/
      embedding_service.py
      index_router.py
      search_service.py

    stores/
      vector_store.py
      keyword_store.py
      symbol_store.py
      file_metadata_store.py

    configuration/
      settings.py

  tests/
    test_embedding_service.py
    test_search_service.py
    test_vector_store.py

  requirements.txt

docs/
  proposed-structure.md
  architecture.md
```

## Where Current Files Move

```text
App.xaml                         -> src/Lavender.App/App.xaml
App.xaml.cs                      -> src/Lavender.App/App.xaml.cs
MainWindow.xaml                  -> src/Lavender.App/MainWindow.xaml
MainWindow.xaml.cs               -> src/Lavender.App/MainWindow.xaml.cs
HeaderToImageConverter.cs        -> src/Lavender.App/Converters/HeaderToImageConverter.cs
Images/*                         -> src/Lavender.App/Assets/Images/

Services/SyntaxHighlighter.cs    -> src/Lavender.App/Rendering/SyntaxHighlighter.cs
Services/RichTextBoxRenderer.cs  -> src/Lavender.App/Rendering/RichTextBoxRenderer.cs

Services/PromptBuilder.cs        -> src/Lavender.Application/Chat/PromptBuilder.cs
Services/OpenAIService.cs        -> src/Lavender.Infrastructure/AI/OpenAIChatClient.cs
Services/FastApiService.cs       -> src/Lavender.Infrastructure/Backend/FastApiClient.cs
                                  -> src/Lavender.Infrastructure/Backend/FastApiProcessManager.cs

Services/ProjectScanner.cs       -> src/Lavender.Infrastructure/FileSystem/ProjectScanner.cs
Services/FileParser.cs           -> src/Lavender.Infrastructure/FileSystem/FileReader.cs
Services/ProjectIndexer.cs       -> src/Lavender.Infrastructure/Indexing/ProjectIndexer.cs

Chunking/CodeChunk.cs            -> src/Lavender.Core/Chunking/CodeChunk.cs
Chunking/CodeChunkService.cs     -> src/Lavender.Infrastructure/Chunking/RoslynCodeChunker.cs
Search/ProjectSearchService.cs   -> src/Lavender.Infrastructure/Retrieval/KeywordContextRetriever.cs
Search/KeywordExtractor.cs       -> src/Lavender.Infrastructure/Retrieval/KeywordExtractor.cs
Search/SearchResult.cs           -> src/Lavender.Core/Retrieval/RetrievalResult.cs

AI_Services/main.py              -> backend/app/main.py
```

## Data Storage Direction

Do not treat "chunking and embeddings" as the only context system. Treat them as one index among several.

Recommended index types:

- Vector index: semantic search over code chunks and summaries.
- Keyword index: exact-ish search for file names, method names, strings, comments, and identifiers.
- Symbol index: classes, methods, properties, interfaces, namespaces, and signatures.
- Reference index: "where is this symbol used?"
- File metadata index: path, extension, hash, modified time, line count, language.
- Open editor context: files/tabs the user currently has open.
- Conversation memory: recent assistant/user messages and durable user preferences.
- Unity context later: scene objects, components, selected object, console messages, assets.

In C#, all of these should return the same kind of object:

```csharp
public sealed class RetrievedContext
{
    public string SourceId { get; init; } = "";
    public ContextSourceKind SourceKind { get; init; }
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public double Score { get; init; }
}
```

That gives the prompt builder one consistent format even when the context came from vector search, keyword search, symbol search, diagnostics, Git, or Unity.

## How The Pieces Interact

1. The WPF app opens a project and creates a `ProjectContext`.
2. `IndexProjectHandler` asks `IProjectScanner` for files.
3. `ProjectIndexer` checks file hashes through `IIndexStateStore` so unchanged files can be skipped later.
4. For each changed file, `ICodeChunker` creates semantic chunks such as whole-file, file-summary, class-fields, methods, constructors, properties, records, structs, interfaces, and enums.
5. The indexer writes records into multiple stores:
   - vector records go to the Python backend/LanceDB;
   - keyword/file metadata can stay in local JSON or SQLite;
   - symbol/reference indexes can be built with Roslyn.
6. When the user asks a question, `AskQuestionHandler` creates a `RetrievalRequest`.
7. `ContextRetrievalService` runs multiple retrievers:
   - semantic vector search;
   - keyword search;
   - symbol search;
   - selected/open file retriever;
   - later Git, diagnostics, and Unity retrievers.
8. `ContextRanker` merges duplicates and ranks results.
9. `ContextBudgetManager` trims the result set so the prompt is not too large.
10. `ContextAssembler` turns retrieved context into a clean context package.
11. `PromptBuilder` creates the final model prompt.
12. `IAIClient` sends the prompt to OpenAI and returns the assistant response to the WPF UI.

## Main Refactor Rule

Keep the UI, application workflow, core models, and infrastructure separate:

- UI should not know LanceDB, FastAPI routes, Roslyn syntax nodes, or OpenAI SDK details.
- Core should not know WPF, HTTP, Python, OpenAI, or LanceDB.
- Application should coordinate workflows but avoid low-level file parsing and HTTP details.
- Infrastructure should contain concrete implementations and can depend on external libraries.

This makes it much easier to add new context sources without rewriting chat or indexing every time.
