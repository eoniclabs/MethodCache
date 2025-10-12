#nullable enable

using System.Text.RegularExpressions;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Compiled Regex Patterns ========================
        private static readonly Regex DynamicTagParameterRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);
    }
}

