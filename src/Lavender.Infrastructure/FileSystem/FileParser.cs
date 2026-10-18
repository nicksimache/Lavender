using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Infrastructure.FileSystem
{
    // Used by the legacy PromptBuilder; active agent reads go through MCP file tools.
    internal class FileParser
    {
        public string ReadFile(string path)
        {
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Retrieves the full paths of all C# source files within the specified project
        /// directory and its subdirectories.
        /// </summary>
        /// <param name="root"></param>
        /// <returns>
        /// An enumerable collection containing the full paths of all discovered
        /// </returns>
        public IEnumerable<string> GetAllCodeFiles(string root)
        {
            return Directory.GetFiles(root, "*.cs",
                SearchOption.AllDirectories);
        }
    }
}
