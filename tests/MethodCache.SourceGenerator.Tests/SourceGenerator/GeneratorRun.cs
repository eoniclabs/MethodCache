using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>Runs MethodCache's generator on projects made in the test, which can reference each other.</summary>
    internal static class GeneratorRun
    {
        internal sealed record RunResult(Compilation Output, IReadOnlyList<Diagnostic> Diagnostics)
        {
            public string Source(string fileName) => Output.SyntaxTrees.Single(t => t.FilePath.EndsWith(fileName, StringComparison.Ordinal)).ToString();
        }

        internal static RunResult Run(CSharpCompilation compilation, bool allowErrors = false)
        {
            CSharpGeneratorDriver.Create(new MethodCacheGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(allowErrors || errors.Count == 0, string.Join(Environment.NewLine, errors));
            return new RunResult(output, diagnostics);
        }

        internal static CSharpCompilation Compilation(string name, string source, params MetadataReference[] references)
            => Compilation(name, new[] { source }, references);

        internal static CSharpCompilation Compilation(string name, string source, string more, params MetadataReference[] references)
            => Compilation(name, new[] { source, more }, references);

        internal static CSharpCompilation Compilation(string name, string[] sources, MetadataReference[] references)
            => CSharpCompilation.Create(
                name,
                sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest))),
                References().Concat(references),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        internal static MetadataReference Image(Compilation compilation)
        {
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return MetadataReference.CreateFromImage(image.ToArray());
        }

        // The framework plus MethodCache and its dependencies, without the test framework, which makes a test project
        internal static IEnumerable<MetadataReference> References()
            => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                .Where(path => !Path.GetFileName(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
                .GroupBy(Path.GetFileName)
                .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()));
    }
}
