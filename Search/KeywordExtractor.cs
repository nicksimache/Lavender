using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Search
{
    internal class KeywordExtractor
    {
        #region Stop Words

        private static readonly HashSet<string> StopWords = new()
        {
            // English
            "a",
            "an",
            "and",
            "are",
            "as",
            "at",
            "be",
            "by",
            "do",
            "does",
            "did",
            "for",
            "from",
            "has",
            "have",
            "how",
            "if",
            "in",
            "into",
            "is",
            "it",
            "its",
            "of",
            "on",
            "or",
            "that",
            "the",
            "their",
            "there",
            "this",
            "to",
            "was",
            "were",
            "what",
            "when",
            "where",
            "which",
            "who",
            "why",
            "with",

            // AI / Natural language filler
            "about",
            "can",
            "could",
            "describe",
            "explain",
            "give",
            "list",
            "please",
            "show",
            "tell",
            "would",

            // Weak code-search words
            "code",
            "file",
            "files",
            "function",
            "functions",
            "member",
            "members",
            "method",
            "methods",
            "object",
            "objects",
            "property",
            "properties",
            "script",
            "scripts",
            "variable",
            "variables"
        };

        #endregion

        public static List<string> Extract(string prompt)
        {
            List<string> tokens = Tokenize(prompt);

            tokens = tokens
                .Where(word => !StopWords.Contains(word.ToLowerInvariant()))
                .ToList();

            tokens = GeneratePhrases(tokens);
            tokens = GenerateIdentifierVariations(tokens);
            tokens = NormalizeLowercase(tokens);
            tokens = RemoveShortWords(tokens);
            tokens = RemoveDuplicates(tokens);
            tokens = RemoveDuplicates(tokens);

            return tokens;
        }

        /// <summary>
        /// Tokenize user prompt such that punctuation is removed, input tokenized on all whitespace
        /// </summary>
        /// <param name="prompt"></param>
        /// <returns></returns>
        public static List<string> Tokenize(string prompt)
        {
            var sb = new StringBuilder();

            foreach (char c in prompt)
            {
                if (c == '_' || !char.IsPunctuation(c)) { sb.Append(c); }
            }

            return sb.ToString()
                 .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                 .ToList();
        }

        /// <summary>
        /// Generates new token variations for each token if camelcase, pascalcase, or snakecase
        /// </summary>
        /// <param name="tokens"></param>
        /// <returns></returns>
        public static List<string> GenerateIdentifierVariations(List<string> tokens)
        {
            var result = new List<string>();

            foreach (string word in tokens)
            {
                result.Add(word);
                int i = 1;
                int lastToken = 0;

                while (i < word.Length)
                {
                    if (char.IsUpper(word[i]) && char.IsLower(word[i - 1]))
                    {
                        result.Add(word[lastToken..i]);
                        lastToken = i;
                    } 
                    else if (word[i] == '_')
                    {
                        result.Add(word[lastToken..i]);
                        lastToken = i + 1;
                    }
                    i++;
                }

                if (lastToken != 0)
                {
                    result.Add(word[lastToken..]);
                }

            }

            return result;
        }

        private static List<string> NormalizeLowercase(List<string> tokens)
        {
            return tokens
                .Select(t => t.ToLowerInvariant())
                .ToList();
        }

        private static List<string> RemoveDuplicates(List<string> tokens)
        {
            return tokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GeneratePhrases(List<string> tokens)
        {
            var result = new List<string>(tokens);

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                result.Add($"{tokens[i]} {tokens[i + 1]}");
            }

            return result;
        }

        private static List<string> RemoveShortWords(List<string> tokens)
        {
            return tokens
                .Where(t => t.Length >= 3)
                .ToList();
        }
    }
}
