#nullable enable

using System.Text.RegularExpressions;

namespace MethodCache.SourceGenerator
{
    public sealed partial class MethodCacheGenerator
    {
        // ======================== Compiled Regex Patterns ========================
        private static readonly Regex DynamicTagParameterRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);

        // Anything in braces that starts like a name but isn't one, such as "{product.Id}", "{ id }" or "{date:yyyy}"
        private static readonly Regex MalformedTagPlaceholderRegex = new(@"\{(\s*[\p{L}_][^{}]*)\}", RegexOptions.Compiled);
    }
}

