using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace MethodCache.Docs.Tests;

public class PublicSnippetApiTests
{
    [Fact]
    public void PublishedSnippets_OnlyReferencePublicLibraryApi()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MethodCache.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory!.FullName, "tests", "MethodCache.Docs.Tests");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .Append(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using System.Net.Http;"))
            .ToArray();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("MethodCache.Docs.Tests", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.NotNull(compilation.GetTypeByMetadataName("MethodCache.ICacheManager"));
        var violations = new List<string>();
        foreach (var tree in trees.Where(tree => tree.FilePath.Contains(Path.DirectorySeparatorChar + "Snippets" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var model = compilation.GetSemanticModel(tree);
            var text = tree.GetText();
            var inSnippet = false;
            var ranges = new List<(int Start, int End)>();
            var start = 0;
            foreach (var line in text.Lines)
            {
                if (line.ToString().Contains("// begin-snippet:", StringComparison.Ordinal)) { inSnippet = true; start = line.End; }
                if (inSnippet && line.ToString().Contains("// end-snippet", StringComparison.Ordinal)) { ranges.Add((start, line.Start)); inSnippet = false; }
            }
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<NameSyntax>()
                .Where(name => ranges.Any(range => name.SpanStart >= range.Start && name.Span.End <= range.End)))
            {
                var info = model.GetSymbolInfo(name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is IAliasSymbol alias) symbol = alias.Target;
                if (symbol?.ContainingAssembly?.Name is not { } assembly || assembly == "MethodCache.Docs.Tests" || !assembly.StartsWith("MethodCache.", StringComparison.Ordinal)) continue;
                if (symbol is INamespaceSymbol or ITypeParameterSymbol) continue;
                if (symbol.DeclaredAccessibility is Accessibility.Internal or Accessibility.Private ||
                    ContainingTypes(symbol).Any(type => type.DeclaredAccessibility != Accessibility.Public))
                    violations.Add($"{Path.GetRelativePath(root, tree.FilePath)}:{name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {symbol.ToDisplayString()}");
            }
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.ContainingType) yield return type;
    }
}
