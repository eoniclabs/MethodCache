using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCheck;
using MethodCache.Core.Tests.Core;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Properties of tag templates such as <c>"user:{userId}"</c>. A cached entry gets its tags from
/// <see cref="CacheTagTemplates.Expand"/>; <c>[CacheInvalidate]</c> builds the tags it removes with
/// <c>string.Format</c> from a template the source generator prepares. When the two disagree for some argument, the
/// invalidation misses the entry and callers keep getting stale data. Seeds and replay: see <see cref="PropertySettings"/>.
/// </summary>
[Collection(nameof(CpuIntensiveCollection))]
public class TagTemplatePropertyTests
{
    private static readonly string[] NamePool = { "id", "userId", "org", "x", "Name" };

    /// <summary>Distinct (case-insensitively, for these fallback-matching cases) parameter names.</summary>
    private static readonly Gen<string[]> GenNames =
        Gen.Int[1, (1 << NamePool.Length) - 1].Select(mask => NamePool.Where((_, i) => (mask & (1 << i)) != 0).ToArray());

    /// <summary>Argument text as the generated code formats it, including text that looks like a placeholder.</summary>
    private static readonly Gen<string> GenValue = Gen.Frequency(
        (3, Gen.String[Gen.Char["ab1:{}\\ "], 0, 6]),
        (1, Gen.OneOfConst("{id}", "{userId}", "{ID}", "{org}", "{x}{id}", "{", "}", "null", "", "ı", "İ")));

    private static Gen<string> GenTag(string[] names) =>
        Gen.OneOf(
                Gen.OneOfConst("user:", "a", ":", "{", "}", "{{", "{other}", "{ id}"),
                Gen.OneOfConst(names).Select(n => "{" + n + "}"),
                Gen.OneOfConst(names).Select(n => "{" + n.ToUpperInvariant() + "}"))
            .Array[0, 5]
            .Select(parts => string.Concat(parts));

    private static readonly Gen<(string[] Names, string[] Values, string[] Tags)> GenCase =
        GenNames.SelectMany(names => Gen.Select(
            GenValue.Array[names.Length],
            GenTag(names).Array[1, 3],
            (values, tags) => (names, values, tags)));

    [Fact]
    public void Expand_CaseDistinctNames_UsesExactMatchAndLeavesAmbiguityLiteral()
    {
        Gen.Select(GenValue, GenValue).Sample((lower, upper) =>
        {
            var result = CacheTagTemplates.Expand(Policy("{name}", "{Name}", "{NAME}"),
                new[] { "name", "Name" }, new[] { lower, upper });
            Assert.Equal(new[] { lower, upper, "{NAME}" }, result.Tags);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void Expand_BuildsTheTagsThatCacheInvalidateRemoves()
    {
        GenCase.Sample(c =>
        {
            var expanded = CacheTagTemplates.Expand(Policy(c.Tags), c.Names, c.Values).Tags;
            var invalidated = c.Tags.Select(tag => InvalidationTag(tag, c.Names, c.Values)).ToArray();

            Assert.Equal(invalidated, expanded);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: Print);
    }

    [Fact]
    public void Expand_IsDeterministicAndCultureIndependent()
    {
        GenCase.Sample(c =>
        {
            var expected = CacheTagTemplates.Expand(Policy(c.Tags), c.Names, c.Values).Tags.ToArray();
            foreach (var culture in PropertySettings.Cultures)
            {
                var again = PropertySettings.InCulture(culture, () => CacheTagTemplates.Expand(Policy(c.Tags), c.Names, c.Values).Tags.ToArray());
                Assert.Equal(expected, again);
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: Print);
    }

    [Fact]
    public void Expand_GivesDistinctArgumentsDistinctTags()
    {
        // One placeholder between fixed text: the tag must identify the argument. (With two placeholders the
        // arguments' text can shift between them, as in "a:{x}:{y}" with ("1:2", "3") and ("1", "2:3"). Such tags
        // only make an invalidation remove more entries than needed, never fewer, so that is accepted.)
        Gen.Select(Gen.OneOfConst(NamePool), Gen.OneOfConst("user:", "", "{", "a{b}:"), Gen.OneOfConst("", ":x", "}"), GenValue, GenValue)
            .Sample((name, prefix, suffix, v1, v2) =>
            {
                var policy = Policy(prefix + "{" + name + "}" + suffix);
                var t1 = CacheTagTemplates.Expand(policy, new[] { name }, new[] { v1 }).Tags.Single();
                var t2 = CacheTagTemplates.Expand(policy, new[] { name }, new[] { v2 }).Tags.Single();

                Assert.Equal(v1 == v2, t1 == t2);
            }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    /// <summary>
    /// What a generated <c>[CacheInvalidate]</c> method builds for <paramref name="tag"/>: a copy of the source
    /// generator's ParseDynamicTags (placeholders <c>\{(\w+)\}</c> naming a parameter, case-insensitively; other
    /// braces doubled) followed by <c>string.Format</c>, which substitutes every placeholder in one pass.
    /// </summary>
    private static string InvalidationTag(string tag, string[] names, string[] values)
    {
        var index = names.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);
        var template = new StringBuilder();
        var arguments = new List<string>();
        var position = 0;
        foreach (Match match in Regex.Matches(tag, @"\{(\w+)\}"))
        {
            if (!index.TryGetValue(match.Groups[1].Value, out var i))
            {
                continue;
            }

            template.Append(Escape(tag.Substring(position, match.Index - position)));
            arguments.Add(values[i]);
            template.Append('{').Append(arguments.Count - 1).Append('}');
            position = match.Index + match.Length;
        }

        if (arguments.Count == 0)
        {
            return tag; // a tag without placeholders for this method's parameters stays as written
        }

        template.Append(Escape(tag.Substring(position)));
        return string.Format(CultureInfo.InvariantCulture, template.ToString(), arguments.ToArray<object>());

        static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");
    }

    private static CacheRuntimePolicy Policy(params string[] tags) => new() { MethodId = "Test.Method", Tags = tags };

    private static string Print((string[] Names, string[] Values, string[] Tags) c)
        => $"names [{string.Join(", ", c.Names)}], values [{string.Join(", ", c.Values.Select(v => '"' + v + '"'))}], tags [{string.Join(", ", c.Tags.Select(t => '"' + t + '"'))}]";
}
