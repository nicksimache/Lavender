using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Lavender.Services
{
    internal class ProjectScanner
    {
        public string ProjectPath { get; }

        public ProjectScanner(string projectPath)
        {
            ProjectPath = projectPath;
        }

        public static bool ShouldIgnoreFile(string path)
        {
            string extension = Path.GetExtension(path);
            string fileName = Path.GetFileName(path);

            return extension.Equals(".meta", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldIgnoreFolder(string path)
        {
            string folderName = Path.GetFileName(path);

            return folderName.Equals("Library", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("Logs", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals(".vs", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("Build", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("Builds", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets list of all files of the specified extensions
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
        public IEnumerable<string> GetFiles(params string[] extensions)
        {
            var normalizedExtensions = extensions
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return EnumerateFiles(ProjectPath)
                .Where(file =>
                    normalizedExtensions.Contains(Path.GetExtension(file)) &&
                    !ShouldIgnoreFile(file));
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            foreach (var file in Directory.GetFiles(root))
            {
                yield return file;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                if (ShouldIgnoreFolder(directory))
                    continue;

                foreach (var file in EnumerateFiles(directory))
                {
                    yield return file;
                }
            }
        }
    }
}
