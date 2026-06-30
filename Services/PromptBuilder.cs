using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Services
{
    internal class PromptBuilder
    {
        private readonly FileParser _fileParser;
        public PromptBuilder(FileParser fileParser)
        {
            _fileParser = fileParser;
        }

        public string PromptOnFileContext(List<string> contextFiles, List<string> retrievedFiles, string prompt)
        {
            string contextFilesString = "";
            string retrievedFilesString = "";
            
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

            foreach (var path in retrievedFiles)
            {
                retrievedFilesString += $"""
                    File:
                    {path}
                    
                    Source Code:
                    
                    ```csharp
                    {_fileParser.ReadFile(path)}
                    ```

                    """;
            }
            return BuildPrompt(contextFilesString, retrievedFilesString, prompt);
        }

        private string BuildPrompt(string contextFiles, string retrievedFiles, string question)
        {
            return $"""
                You are an expert C# and Unity programming assistant.

                You are helping answer questions about the user's Unity project.

                You may be given:
                - One or more files explicitly selected by the user.
                - Additional files retrieved automatically because they appear relevant to the user's question.
                - No project files at all.

                Only use the provided project files when answering.

                If the provided files are insufficient to answer the question, clearly explain what additional files or information would be needed. Do not invent code or assume implementations that are not shown.

                When discussing code:
                - Reference the relevant class, method, property, variable, or file names.
                - Prefer concise explanations.
                - If multiple files are provided, explain how they relate when appropriate.

                For broad questions such as "What does this system do?" or "Explain this file":
                - Begin with a short summary.
                - Follow with 3–5 important bullet points.
                - Focus on the most important classes, methods, and interactions.
                - Do not explain every line unless requested.

                ======================
                PROJECT CONTEXT
                ======================

                {contextFiles}

                ======================
                AUTOMATICALLY RETRIEVED FILES FROM KEYWORD SEARCH
                ======================

                {retrievedFiles}

                ======================
                USER QUESTION
                ======================

                {question}
                """;
        }

    }
}
