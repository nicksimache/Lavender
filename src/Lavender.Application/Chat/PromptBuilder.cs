using Lavender.Core.DataTypes;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.Application.Chat
{
    internal class PromptBuilder
    {
        private readonly FileParser _fileParser;

        public PromptBuilder(FileParser fileParser)
        {
            _fileParser = fileParser;
        }

        public string PromptOnFileContext(
            List<string> contextFiles,
            List<VectorSearchCodeChunk> semanticSearchedChunks,
            string prompt)
        {
            string contextFilesString = "";

            foreach (string path in contextFiles)
            {
                contextFilesString += $"""
                    File:
                    {path}
                    
                    Source Code:
                    
                    ```csharp
                    {_fileParser.ReadFile(path)}
                    ```

                    """;
            }

            string semanticSearchedChunksString = SemanticSearchedChunksToString(semanticSearchedChunks);

            return BuildPrompt(contextFilesString, semanticSearchedChunksString, prompt);
        }

        private string BuildPrompt(string selectedFiles, string semanticSearchedChunksString, string question)
        {
            return $"""
                You are Lavender, an expert C# and Unity programming assistant.

                You are helping answer questions about the user's project.

                You are provided with project code that has been retrieved using semantic vector search. The retrieved code chunks are the parts of the project most likely to answer the user's question, but they may not represent the entire implementation.

                Guidelines:

                - Answer using only the provided code.
                - Do not invent implementations, methods, or behaviors that are not shown.
                - If the provided code is insufficient to answer confidently, explain what additional code or files would be needed.
                - Reference file names, classes, methods, properties, and line numbers whenever possible.
                - If multiple chunks work together, explain how they relate.
                - If multiple implementations are possible, state which one is supported by the provided code.
                - Keep responses concise unless the user requests a detailed explanation.

                For broad questions such as:
                - "What does this system do?"
                - "Explain this class."
                - "How does this feature work?"

                Begin with a short summary followed by the most important implementation details.

                ======================
                USER SELECTED FILES
                ======================

                These files were explicitly selected by the user and should be considered the highest priority context.

                {selectedFiles}

                ======================
                SEMANTIC SEARCH RESULTS
                ======================

                The following code chunks were retrieved automatically because they are semantically relevant to the user's question.

                Some chunks may reference code that is not included below.

                {semanticSearchedChunksString}

                ======================
                USER QUESTION
                ======================

                {question}
                """;
        }

        private string SemanticSearchedChunksToString(List<VectorSearchCodeChunk> chunks)
        {
            string res = "";
            int i = 0;

            foreach (var chunk in chunks)
            {
                int similarity = 0;
                if (chunk.Distance < 0)
                {
                    similarity = -1;
                }

                res += $"""
                    ---------------------- Start of Chunk {i} ----------------------
                    File: {chunk.FilePath}
                    
                    Lines: {chunk.StartLine}-{chunk.EndLine}
                    
                    Namespace: {chunk.Namespace}
                    
                    Class: {chunk.ClassName}
                    
                    Member: {chunk.MemberName}
                    
                    Signature: {chunk.Signature}
                    
                    Similarity {1 - similarity}
                    
                    Code:
                    ```csharp
                    {chunk.Code}
                    ```
                    ----------------------  End of Chunk {i}  ----------------------

                    """;

                i++;
            }

            return res;
        }
    }
}
