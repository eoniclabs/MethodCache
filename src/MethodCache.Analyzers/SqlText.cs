using System;
using System.Collections.Generic;
using System.Linq;

namespace MethodCache.Analyzers
{
    /// <summary>
    /// The words of a SQL text, enough to tell what it may write: identifiers and keywords, with quoted identifiers
    /// unquoted, and string literals, comments, numbers and punctuation left out. It parses no grammar, so where it can't
    /// tell, the analyzer treats the SQL as a write.
    /// </summary>
    internal static class SqlText
    {
        /// <summary>A word, and whether it is the first word of its statement.</summary>
        internal readonly struct Word
        {
            public Word(string text, bool first)
            {
                Text = text;
                First = first;
            }

            public string Text { get; }

            public bool First { get; }

            public bool Is(string keyword) => string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
        }

        public static List<Word> Words(string sql)
        {
            var words = new List<Word>();
            var first = true;
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (c == ';')
                {
                    first = true;
                    i++;
                }
                else if (c == '-' && At(sql, i + 1, '-'))
                {
                    var end = sql.IndexOf('\n', i);
                    i = end < 0 ? sql.Length : end + 1;
                }
                else if (c == '/' && At(sql, i + 1, '*'))
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                }
                else if (c == '\'')
                {
                    // A string literal, with '' for a quote inside it
                    i++;
                    while (i < sql.Length && (sql[i] != '\'' || At(sql, i + 1, '\''))) i += sql[i] == '\'' ? 2 : 1;
                    i++;
                }
                else if (c is '"' or '[' or '`')
                {
                    var close = c == '[' ? ']' : c;
                    var end = sql.IndexOf(close, i + 1);
                    if (end < 0) end = sql.Length;
                    words.Add(new Word(sql.Substring(i + 1, end - i - 1), first));
                    first = false;
                    i = end + 1;
                }
                else if (char.IsLetterOrDigit(c) || c == '_')
                {
                    var start = i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                    if (!char.IsDigit(c)) words.Add(new Word(sql.Substring(start, i - start), first));
                    first = false;
                }
                else
                {
                    i++;
                }
            }
            return words;
        }

        /// <summary>
        /// Every statement starts with <c>select</c>, such as an advisory lock, or <c>set</c>, such as
        /// <c>SET LOCAL lock_timeout</c>.
        /// </summary>
        public static bool OnlySelectsOrSets(List<Word> words)
            => words.Count > 0 && words.Where(w => w.First).All(w => w.Is("select") || w.Is("set"));

        /// <summary>A statement runs code the analyzer can't see into: a stored procedure or an anonymous block.</summary>
        public static bool RunsCode(List<Word> words)
            => words.Any(w => w.First && (w.Is("call") || w.Is("exec") || w.Is("execute") || w.Is("do")));

        /// <summary>A name compared without case and underscores, so <c>ai_model_prices</c> and <c>AiModelPrices</c> are equal.</summary>
        public static string Normalize(string name)
        {
            var chars = new char[name.Length];
            var length = 0;
            foreach (var c in name)
            {
                if (c != '_') chars[length++] = char.ToLowerInvariant(c);
            }
            return new string(chars, 0, length);
        }

        private static bool At(string text, int index, char c) => index < text.Length && text[index] == c;
    }
}
