namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Matches tags against simple wildcard patterns: <c>*</c> matches any sequence (including empty),
/// <c>?</c> matches exactly one character, everything else is literal and the whole tag must match.
/// Allocation-free and linear for typical patterns, with no regular-expression backtracking risk.
/// </summary>
internal static class WildcardPattern
{
    public static bool IsMatch(string pattern, string text)
    {
        int p = 0, t = 0;
        int starPattern = -1, starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
