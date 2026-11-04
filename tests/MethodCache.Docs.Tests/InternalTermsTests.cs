using System.Text.RegularExpressions;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// The guides explain what a user does and sees. Words for how MethodCache works inside belong in
/// docs/developer, and exact rules in docs/reference, written without them (see AGENTS.md).
/// </summary>
public class InternalTermsTests
{
    private static readonly Regex InternalTerm = new(
        @"\b(epochs?|generations?|publications?|retire[sd]?|owned|tombstones?|wire|contract[ -]1|L1|L2|snapshots?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Code (API names such as PublicationQueued, metric values) and link targets are not prose
    private static readonly Regex CodeBlock = new(@"^(```|~~~).*?^\1", RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex InlineCode = new(@"`[^`\n]*`");
    private static readonly Regex LinkTarget = new(@"\]\([^)]*\)");
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline);

    public static TheoryData<string> Guides()
    {
        var root = DemoWebFactory.RepositoryRoot;
        var data = new TheoryData<string>();
        IEnumerable<string> files =
        [
            Path.Combine(root, "README.md"),
            Path.Combine(root, "NUGET_README.md"),
            .. Directory.GetFiles(Path.Combine(root, "docs"), "*.md"),
            .. Directory.GetFiles(Path.Combine(root, "docs", "features"), "*.md"),
            .. Directory.GetFiles(Path.Combine(root, "docs", "ai"), "*.md"),
            .. Directory.GetFiles(Path.Combine(root, "docs", "user-guide"), "*.md"),
            .. Directory.GetDirectories(Path.Combine(root, "src"))
                .Select(d => Path.Combine(d, "README.md"))
                .Where(File.Exists),
        ];
        foreach (var file in files.Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Guides))]
    public void Guide_UsesNoInternalTerms(string page)
    {
        var text = File.ReadAllText(Path.Combine(DemoWebFactory.RepositoryRoot, page));
        text = HtmlComment.Replace(CodeBlock.Replace(text, ""), "");
        text = LinkTarget.Replace(InlineCode.Replace(text, ""), "]");

        var found = text.Split('\n')
            .Select((line, index) => (Line: index + 1, Match: InternalTerm.Match(line)))
            .Where(l => l.Match.Success)
            .Select(l => $"'{l.Match.Value}' (line {l.Line} after removing code)")
            .ToList();

        Assert.True(found.Count == 0,
            $"{page} uses internal terms: {string.Join(", ", found)}. Say what the user sees instead, and put exact rules in docs/reference.");
    }
}
