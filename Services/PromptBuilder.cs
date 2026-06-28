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

        public string PromptOnFileContext(List<string> filePaths, string prompt)
        {
            if (filePaths.Count == 0) { return prompt; }
            string files = "";
            
            foreach (string path in filePaths)
            {
                files += $"""
                    File:
                    {path}
                    
                    Source Code:
                    
                    ```csharp
                    {_fileParser.ReadFile(path)}
                    ```

                    """;
            }
            return BuildPrompt(files, prompt);
        }

        private string BuildPrompt(string fileContent, string question)
        {
        return $"""
            You are an expert C# and Unity programming assistant.
                
            You have been given a single source file from the user's project.

            Answer the user's question using only the information contained in this file.

            If the answer cannot be determined from this file alone, clearly state that and explain what additional files or information would be needed. Do not invent or assume code that is not shown.

            When referencing code, mention the relevant class, method, property, or variable names.

            Keep your answer concise by default.

            For broad questions like "what does this file do?", answer in:
            - 1 short summary paragraph
            - 3-5 bullet points max
            - only mention important classes/methods

            Do not explain every line unless the user asks for a detailed walkthrough.
            
            {fileContent}

            User Question:
            {question}
            """;
        }

    }
}
