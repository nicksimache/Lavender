using Lavender.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lavender.Search
{
    internal class ProjectSearchService
    {
        private readonly ProjectScanner _projectScanner;

        public ProjectSearchService(ProjectScanner projectScanner)
        {
            _projectScanner = projectScanner;
        }

        public List<SearchResult> Search(string prompt)
        {
            var results = new List<SearchResult>();
            var keywords = KeywordExtractor.Extract(prompt);

            foreach (var file in _projectScanner.GetFiles(".cs"))
            {
                string fileContents = File.ReadAllText(file);
                int score = ScoreMatches(file, fileContents, keywords);

                if (score > 0)
                {
                    results.Add(new SearchResult
                    {
                        FilePath = file,
                        Score = score
                    });
                    Debug.WriteLine($"Filepath: {file}, Score: {score}");
                }
            }

            return results
                .OrderByDescending(r => r.Score)
                .Take(5)
                .ToList();
        }

        private static int ScoreMatches(string file, string text, IEnumerable<string> keywords)
        {
            int score = 0;
            string fileName = Path.GetFileNameWithoutExtension(file);

            foreach (string keyword in keywords)
            {
                string escaped = Regex.Escape(keyword);

                if (fileName.Contains(keyword))
                {
                    score += 25;
                }
                else if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                }

                string pattern = Regex.Escape(keyword).Replace(@"\ ", @"[\s_]*");

                score += Regex.Matches(text, $@"\b(class|struct|interface|enum)\s+\w*{pattern}\w*", RegexOptions.IgnoreCase).Count * 15;
                score += Regex.Matches(text, $@"\b\w+\s+\w*{pattern}\w*\s*\(", RegexOptions.IgnoreCase).Count * 12;
                score += Regex.Matches(text, $@"\b\w+\s+\w*{pattern}\w*\s*(=|{{|;)", RegexOptions.IgnoreCase).Count * 8;
                score += Regex.Matches(text, $@"\w*{pattern}\w*\s*\(", RegexOptions.IgnoreCase).Count * 10;
                score += Regex.Matches(text, $@"//.*{pattern}", RegexOptions.IgnoreCase).Count * 3;
                score += Regex.Matches(text, $@"""[^""]*{pattern}[^""]*""", RegexOptions.IgnoreCase).Count * 2;
                score += Regex.Matches(text, pattern, RegexOptions.IgnoreCase).Count;
            }

            return score;
        }
    }
}
