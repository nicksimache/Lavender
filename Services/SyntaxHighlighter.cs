using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lavender.Services
{
    public class SyntaxHighlighter
    {
        public class CodeSpan
        {
            public CodeSpan(string text, string color)
            {
                Text = text;
                Color = color;
            }

            public string Text { get; set; } = "";
            public string Color { get; set; } = "#D1D5DB";
        }

        public static List<CodeSpan> HighlightCSharpCode(string code)
        {
            var spans = new List<CodeSpan>();
            int i = 0;

            while (i < code.Length) 
            {
                char current = code[i];

                if (StartsWithLineComment(code, i))
                {
                    string comment = ReadUntilNewLine(code, ref i);
                    spans.Add(new CodeSpan(comment, "#6A9955"));
                }
                else if (StartsWithBlockComment(code, i))
                {
                    string comment = ReadBlockComment(code, ref i);
                    spans.Add(new CodeSpan(comment, "#6A9955"));
                }
                else if(StartsWithLiteral(code, i))
                {
                    string str = ReadLiteral(code, ref i);
                    spans.Add(new CodeSpan(str, "#CE9178"));
                }
                else if (char.IsDigit(current))
                {
                    string number = ReadNumber(code, ref i);
                    spans.Add(new CodeSpan(number, "#B5CEA8"));
                }
                else if (char.IsLetter(current) || current == '_')
                {
                    string word = ReadWord(code, ref i);

                    if (IsKeyword(word))
                        spans.Add(new CodeSpan(word, "#C084FC"));
                    else
                        spans.Add(new CodeSpan(word, "#D1D5DB"));
                }
                else
                {
                    spans.Add(new CodeSpan(current.ToString(), "#D1D5DB"));
                    i++;
                }

            }

            return spans;
        }

        #region Checks

        private static bool StartsWithLineComment(string code, int i)
        {
            return i + 1 < code.Length && code[i] == '/' && code[i + 1] == '/';
        }

        private static bool StartsWithBlockComment(string code, int i)
        {
            return i + 1 < code.Length &&
                   code[i] == '/' &&
                   code[i + 1] == '*';
        }

        private static bool StartsWithLiteral(string code, int i)
        {
            return code[i] == '"' || code[i] == '\'';
        }

        private static bool IsKeyword(string word)
        {
            return word is "public" or "private" or "protected"
                or "class" or "struct" or "interface"
                or "void" or "int" or "float" or "bool" or "string"
                or "if" or "else" or "for" or "foreach" or "while"
                or "return" or "new" or "using" or "namespace";
        }

        #endregion

        #region Return Strings

        private static string ReadUntilNewLine(string code, ref int i)
        {
            int start = i;

            while (i < code.Length && code[i] != '\n')
            {
                i++;
            }

            return code[start..i];
        }

        private static string ReadLiteral(string code, ref int i)
        {
            char delimiter = code[i];
            int start = i;

            i++;

            while (i < code.Length)
            {
                // Skip escaped characters (\", \', \\, \n, etc.)
                if (code[i] == '\\' && i + 1 < code.Length)
                {
                    i += 2;
                    continue;
                }

                // Found the matching closing quote
                if (code[i] == delimiter)
                {
                    i++;
                    break;
                }

                i++;
            }

            return code[start..i];
        }

        private static string ReadNumber(string code, ref int i)
        {
            int start = i;

            while (i < code.Length && (char.IsDigit(code[i]) || code[i] == '.'))
            {
                i++;
            }

            return code[start..i];
        }

        private static string ReadWord(string code, ref int i)
        {
            int start = i;

            while (i < code.Length &&
                   (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
            {
                i++;
            }

            return code[start..i];
        }

        private static string ReadBlockComment(string code, ref int i)
        {
            int start = i;

            // Skip /*
            i += 2;

            while (i + 1 < code.Length)
            {
                if (code[i] == '*' && code[i + 1] == '/')
                {
                    i += 2;
                    break;
                }

                i++;
            }

            return code[start..i];
        }

        #endregion

    }
}
