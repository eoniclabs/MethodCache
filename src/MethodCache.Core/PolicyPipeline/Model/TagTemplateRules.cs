using System.Text.RegularExpressions;

namespace MethodCache.Core.PolicyPipeline.Model;

/// <summary>
/// Checks a tag template such as <c>"user:{userId}"</c> against a method's parameter names, by the rules the source
/// generator reports as MC0010, MC0016 and MC0017 for <c>[Cache]</c>, so a template set in code, configuration or
/// a runtime override that would stay literal text is rejected instead.
/// </summary>
internal static partial class TagTemplateRules
{
    // The same patterns as the generator's DynamicTagParameterRegex and MalformedTagPlaceholderRegex
    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder { get; }

    [GeneratedRegex(@"\{(\s*[\p{L}_][^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Malformed { get; }

    /// <summary>
    /// Explains why <paramref name="tag"/> doesn't fit a method with <paramref name="parameterNames"/>, or returns
    /// <see langword="null"/> when every placeholder names one of its parameters. The text doesn't repeat the tag.
    /// </summary>
    public static string? Check(string tag, IReadOnlyList<string> parameterNames)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(parameterNames);
        if (!tag.Contains('{', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (Match match in Placeholder.Matches(tag))
        {
            var name = match.Groups[1].Value;
            if (parameterNames.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var matches = parameterNames.Where(parameter => string.Equals(parameter, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                continue;
            }

            return matches.Length == 0
                ? $"{{{name}}} names no parameter of the method, whose parameters are {Describe(parameterNames)}, so it would stay literal text (MC0010)"
                : $"{{{name}}} matches the parameters {Describe(matches)} when case is ignored, so it would stay literal text; use the exact spelling (MC0017)";
        }

        foreach (Match match in Malformed.Matches(tag))
        {
            if (!Placeholder.IsMatch(match.Value))
            {
                return $"{{{match.Groups[1].Value}}} is not a parameter name, so it would stay literal text; put only a parameter name " +
                       "between the braces, and pass a value such as product.Id as its own parameter (MC0016)";
            }
        }

        return null;
    }

    private static string Describe(IReadOnlyCollection<string> names)
        => names.Count == 0 ? "none" : string.Join(", ", names);
}
