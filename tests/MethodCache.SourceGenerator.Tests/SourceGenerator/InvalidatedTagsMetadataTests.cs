using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>
    /// The generator writes the tags a project invalidates into its assembly, where TagCoverage in a test project reads
    /// them to check tags marked InvalidatedElsewhere.
    /// </summary>
    public class InvalidatedTagsMetadataTests
    {
        private const string Worker = @"
using System.Threading;
using System.Threading.Tasks;
using MethodCache;

namespace Worker
{
    public interface IImports
    {
        [CacheInvalidate(Tags = new[] { ""imports"", ""import:{id}"" })]
        Task ImportAsync(int id);
    }

    public static class Changes
    {
        [InvalidatesCache]
        public static void Changed(string tag) { }
    }

    public sealed class FoodImport(ICacheManager cache, IMethodCacheKeys keys)
    {
        public async Task RunAsync(int id, CancellationToken ct)
        {
            await cache.InvalidateTagAsync(""food-catalog"", ct);
            await cache.InvalidateTagsAsync(new[] { $""food:{id}"", MethodCacheKeys.Tag($""region:{id}"") }, ct);
            Changes.Changed(""recipes"");
            Log(""not a tag"");
        }

        private static void Log(string message) { }
    }
}";

        [Fact]
        public void TheTagsAProjectInvalidates_AreWrittenIntoItsAssembly()
        {
            var generated = Generate(Worker, References());

            Assert.Contains("// The tags this project invalidates", generated);
            foreach (var tag in new[] { "imports", "import:{}", "food-catalog", "food:{}", "region:{}", "recipes" })
                Assert.Contains($"[assembly: global::System.Reflection.AssemblyMetadata(\"MethodCache.InvalidatedTags\", \"{tag}\")]", generated);
            Assert.DoesNotContain("not a tag", generated);
        }

        [Fact]
        public void ATestProject_WritesNoTags()
        {
            var xunit = typeof(FactAttribute).Assembly.Location;

            var generated = Generate(Worker, References().Append(MetadataReference.CreateFromFile(xunit)));

            Assert.DoesNotContain("MethodCache.InvalidatedTags", generated);
        }

        [Fact]
        public void AProjectThatInvalidatesNothing_WritesNoFile()
        {
            var generated = Generate("namespace App { public static class Nothing { public static string Name() => \"x\".Trim(); } }", References());

            Assert.DoesNotContain("MethodCache.InvalidatedTags", generated);
        }

        private static string Generate(string source, IEnumerable<MetadataReference> references)
        {
            Compilation compilation = CSharpCompilation.Create("Worker", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            CSharpGeneratorDriver.Create(new MethodCacheGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out compilation, out var diagnostics);
            Assert.Empty(diagnostics);
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return string.Join("\n", compilation.SyntaxTrees.Skip(1).Select(tree => tree.ToString()));
        }

        // The framework and MethodCache, without the test framework the tests themselves run on
        private static IEnumerable<MetadataReference> References()
            => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
    }
}
