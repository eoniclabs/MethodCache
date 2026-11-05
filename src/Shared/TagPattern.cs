using System.Text;

namespace MethodCache.Internal
{
    // Compiled into the analyzer and MethodCache.Testing, so MC0028 and TagCoverage agree on which tags an invalidation covers.
    internal static class TagPattern
    {
        /// <summary>Stands for any text, also none, in a pattern.</summary>
        internal const char Any = '\0';

        /// <summary>A tag template with each <c>{placeholder}</c> standing for any text.</summary>
        internal static string Of(string template)
        {
            var pattern = new StringBuilder(template.Length);
            for (var i = 0; i < template.Length; i++)
            {
                var end = template[i] == '{' ? template.IndexOf('}', i + 1) : -1;
                if (end < 0)
                {
                    pattern.Append(template[i] == Any ? ' ' : template[i]);
                    continue;
                }
                pattern.Append(Any);
                i = end;
            }
            return pattern.ToString();
        }

        /// <summary>Whether some tag matches both patterns, where <see cref="Any"/> matches any text, also none.</summary>
        internal static bool Overlaps(string a, string b)
        {
            var known = new bool?[a.Length + 1, b.Length + 1];
            return Match(0, 0);

            bool Match(int i, int j)
            {
                if (known[i, j] is { } answer) return answer;
                bool result;
                if (i == a.Length && j == b.Length) result = true;
                else if (i < a.Length && a[i] == Any) result = Match(i + 1, j) || (j < b.Length && Match(i, j + 1));
                else if (j < b.Length && b[j] == Any) result = Match(i, j + 1) || (i < a.Length && Match(i + 1, j));
                else result = i < a.Length && j < b.Length && a[i] == b[j] && Match(i + 1, j + 1);
                known[i, j] = result;
                return result;
            }
        }
    }
}
