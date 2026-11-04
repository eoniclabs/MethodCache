using System.Collections.Immutable;
using MethodCache.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class HandBuiltTagAnalyzerTests
    {
        private const string Header = @"using System;
using System.Threading.Tasks;
using MethodCache;

public enum Tier { Free, Pro }

public class Endpoint
{
    public async Task Run(ICacheManager cache, ICacheAdministrator admin, string model, Guid tenant, Tier tier, int id, bool on, double price)
    {
";
        private const string Footer = @"
    }
}";

        [Theory]
        [InlineData("await cache.InvalidateTagAsync($\"price:{model}\");", "model")]
        [InlineData("await cache.InvalidateTagAsync($\"plan:{tenant}\");", "tenant")]
        [InlineData("await cache.InvalidateTagAsync($\"plan:{tier}\");", "tier")]
        [InlineData("await cache.InvalidateTagAsync($\"on:{on}\");", "on")]
        [InlineData("await cache.InvalidateTagAsync($\"p:{price}\");", "price")]
        [InlineData("await cache.InvalidateTagsAsync([\"all\", $\"price:{model}\"]);", "model")]
        [InlineData("await cache.InvalidateTagsAsync(new[] { $\"plan:{tenant}\" });", "tenant")]
        [InlineData("await admin.InvalidateTagsAsync([$\"plan:{tenant}\"]);", "tenant")]
        [InlineData("await cache.InvalidateTagAsync($\"user:{id:D5}\");", "id")]
        public async Task HandBuiltTag_ValueMethodCacheFormatsDifferently_IsReported(string statement, string value)
        {
            var diagnostics = await AnalyzeAsync(Header + statement + Footer);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("MC0024", diagnostic.Id);
            Assert.Contains($"'{value}'", diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("await cache.InvalidateTagAsync($\"user:{id}\");")]
        [InlineData("await cache.InvalidateTagAsync($\"price:{MethodCacheKeys.Part(model)}\");")]
        [InlineData("await cache.InvalidateTagAsync($\"plan:{MethodCacheKeys.Part(tenant)}:{MethodCacheKeys.Part(tier)}\");")]
        [InlineData("const string Kind = \"rates\"; await cache.InvalidateTagAsync($\"kind:{Kind}\");")]
        [InlineData("await cache.InvalidateTagAsync(\"prices\");")]
        [InlineData("var text = $\"price:{model}\"; Console.WriteLine(text);")]
        public async Task Tag_FormattedAsMethodCacheFormatsIt_IsNotReported(string statement)
        {
            Assert.Empty(await AnalyzeAsync(Header + statement + Footer));
        }

        [Theory]
        [InlineData("await cache.InvalidateTagAsync($\"price:{model}\");", "await cache.InvalidateTagAsync($\"price:{MethodCacheKeys.Part(model)}\");")]
        [InlineData("await cache.InvalidateTagAsync($\"plan:{tenant}\");", "await cache.InvalidateTagAsync($\"plan:{MethodCacheKeys.Part(tenant)}\");")]
        [InlineData("await cache.InvalidateTagAsync($\"plan:{tier}\");", "await cache.InvalidateTagAsync($\"plan:{MethodCacheKeys.Part(tier)}\");")]
        public async Task CodeFix_WrapsTheValueInMethodCacheKeysPart(string statement, string expected)
        {
            var source = Header + statement + Footer;

            var (title, fixedSource) = await FixAsync(source);

            Assert.Equal("Format with MethodCacheKeys.Part", title);
            Assert.Equal(Header + expected + Footer, fixedSource);
            Assert.Empty(await AnalyzeAsync(fixedSource));
        }

        [Fact]
        public async Task CodeFix_WithoutUsingMethodCache_WritesTheFullName()
        {
            var source = @"using System;
using System.Threading.Tasks;

public class Endpoint
{
    public Task Run(MethodCache.ICacheManager cache, Guid tenant) => MethodCache.CacheManagerExtensions.InvalidateTagAsync(cache, $""plan:{tenant}"").AsTask();
}";

            var (_, fixedSource) = await FixAsync(source);

            // Inside an interpolation, "::" would start a format clause, so the call needs parentheses
            Assert.Contains("$\"plan:{(global::MethodCache.MethodCacheKeys.Part(tenant))}\"", fixedSource);
            Assert.Empty(await AnalyzeAsync(fixedSource));
        }

        [Fact]
        public async Task FormattedValue_IsReportedWithoutAFix()
        {
            var source = Header + "await cache.InvalidateTagAsync($\"p:{price:F2}\");" + Footer;

            var diagnostic = Assert.Single(await AnalyzeAsync(source));

            Assert.False(diagnostic.Properties.ContainsKey(HandBuiltTagAnalyzer.FixableProperty));
        }

        [Fact]
        public async Task HandBuiltTag_PassedToInvalidateAfterCommit_IsReported()
        {
            const string source = @"using System;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }
    public static class MethodCacheDbContextExtensions { public static void InvalidateAfterCommit(this DbContext context, params string[] tags) { } }
}

public class Endpoint
{
    public void Run(DbContext db, Guid tenant, int id) => db.InvalidateAfterCommit($""order:{id}"", $""plan:{tenant}"");
}";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));

            Assert.Equal("MC0024", diagnostic.Id);
            Assert.Contains("'tenant'", diagnostic.GetMessage());
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        {
            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                [CSharpSyntaxTree.ParseText(source)],
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HandBuiltTagAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }

        private static async Task<(string Title, string Source)> FixAsync(string source)
        {
            using var workspace = new AdhocWorkspace();
            var document = workspace.CurrentSolution
                .AddProject("Test", "Test", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(References())
                .AddDocument("File.cs", SourceText.From(source));
            var compilation = (await document.Project.GetCompilationAsync())!;
            var diagnostic = (await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HandBuiltTagAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Single();

            var actions = new List<CodeAction>();
            await new FormatTagPartCodeFixProvider().RegisterCodeFixesAsync(
                new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
            var action = Assert.Single(actions);
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return (action.Title, (await changed.GetDocument(document.Id)!.GetTextAsync()).ToString());
        }

        private static IEnumerable<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        }
    }
}
