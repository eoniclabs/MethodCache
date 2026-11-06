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
    /// <summary>
    /// MC0035: a cache factory that uses a token from outside it, which the call doesn't pass, fails the shared load for
    /// every caller when that token is cancelled.
    /// </summary>
    public class FactoryTokenAnalyzerTests
    {
        private const string Header = @"using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache;

public sealed class Request { public CancellationToken Aborted { get; set; } }

public class Endpoint
{
    private readonly CancellationToken _stopping = default;

    private static Task<int> LoadAsync(CancellationToken token) => Task.FromResult(1);
    private static Task<IReadOnlyDictionary<string, int>> LoadManyAsync(IReadOnlyList<string> keys, CancellationToken token) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public async Task Run(ICacheManager cache, Request request, CancellationToken ct, CancellationToken other)
    {
";
        private const string Footer = @"
    }
}";

        [Theory]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(ct));", "ct")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(ct), cancellationToken: other);", "ct")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(request.Aborted));", "request.Aborted")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(_stopping));", "_stopping")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", 1, async (id, _) => await LoadAsync(ct));", "ct")]
        [InlineData("await cache.GetOrCreateManyAsync(new[] { \"a\" }, async (keys, _) => await LoadManyAsync(keys, ct));", "ct")]
        [InlineData("var token = ct; await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(token));", "token")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => { ct.ThrowIfCancellationRequested(); return await LoadAsync(CancellationToken.None); });", "ct")]
        public async Task OuterTokenTheCallDoesNotPass_IsReported(string statement, string token)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + statement + Footer));

            Assert.Equal("MC0035", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Contains($"The factory uses '{token}', which the call doesn't pass as its cancellationToken", diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(token), cancellationToken: ct);")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(ct), cancellationToken: ct);")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(ct), o => o.WithDuration(TimeSpan.FromMinutes(5)), ct);")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(request.Aborted), cancellationToken: request.Aborted);")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(CancellationToken.None));")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(default));")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async _ => { using var cts = new CancellationTokenSource(); return await LoadAsync(cts.Token); });")]
        [InlineData("await cache.GetOrCreateAsync(\"k\", async token => await Task.Run(() => LoadAsync(token)));")]
        [InlineData("await Task.Run(async () => await LoadAsync(ct));")]
        [InlineData("Func<CancellationToken, Task<int>> load = _ => LoadAsync(ct); await load(ct);")]
        public async Task OwnOrPassedToken_IsNotReported(string statement)
        {
            Assert.Empty(await AnalyzeAsync(Header + statement + Footer));
        }

        [Fact]
        public async Task EachUse_IsReported()
        {
            var diagnostics = await AnalyzeAsync(Header + "await cache.GetOrCreateAsync(\"k\", async _ => { ct.ThrowIfCancellationRequested(); return await LoadAsync(ct); });" + Footer);

            Assert.Equal(2, diagnostics.Length);
            Assert.All(diagnostics, d => Assert.Equal("MC0035", d.Id));
        }

        [Theory]
        [InlineData(
            "await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(ct));",
            "await cache.GetOrCreateAsync(\"k\", async ct => await LoadAsync(ct), cancellationToken: ct);",
            "Use the factory's own token and pass 'ct' as cancellationToken")]
        [InlineData(
            "await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(ct), o => o.WithDuration(TimeSpan.FromMinutes(5)));",
            "await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(token), o => o.WithDuration(TimeSpan.FromMinutes(5)), cancellationToken: ct);",
            "Use the factory's own token and pass 'ct' as cancellationToken")]
        [InlineData(
            "await cache.GetOrCreateAsync(\"k\", async _ => await LoadAsync(request.Aborted));",
            "await cache.GetOrCreateAsync(\"k\", async ct => await LoadAsync(ct), cancellationToken: request.Aborted);",
            "Use the factory's own token and pass 'request.Aborted' as cancellationToken")]
        [InlineData(
            "await cache.GetOrCreateAsync(\"k\", 1, async (id, _) => { ct.ThrowIfCancellationRequested(); return await LoadAsync(ct); });",
            "await cache.GetOrCreateAsync(\"k\", 1, async (id, ct) => { ct.ThrowIfCancellationRequested(); return await LoadAsync(ct); }, cancellationToken: ct);",
            "Use the factory's own token and pass 'ct' as cancellationToken")]
        [InlineData(
            "await cache.GetOrCreateManyAsync(new[] { \"a\" }, async (keys, _) => await LoadManyAsync(keys, ct));",
            "await cache.GetOrCreateManyAsync(new[] { \"a\" }, async (keys, ct) => await LoadManyAsync(keys, ct), cancellationToken: ct);",
            "Use the factory's own token and pass 'ct' as cancellationToken")]
        [InlineData(
            "await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(ct), cancellationToken: other);",
            "await cache.GetOrCreateAsync(\"k\", async token => await LoadAsync(token), cancellationToken: other);",
            "Use the factory's own token")]
        public async Task CodeFix_UsesTheFactorysTokenAndPassesTheOuterOne(string statement, string expected, string title)
        {
            var (actualTitle, source) = await FixAsync(Header + statement + Footer);

            Assert.Equal(title, actualTitle);
            Assert.Equal(Header + expected + Footer, source);
            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task CodeFix_ADiscardWhoseOuterTokenNameIsTaken_GetsAnotherName()
        {
            var (_, source) = await FixAsync(Header + "await cache.GetOrCreateAsync(\"k\", async _ => { var ct = 1; return await LoadAsync(request.Aborted); });" + Footer);

            Assert.Contains("async token => { var ct = 1; return await LoadAsync(token); }, cancellationToken: request.Aborted);", source);
            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task FixAll_FixesEveryFactoryInTheDocument()
        {
            var statements = "await cache.GetOrCreateAsync(\"a\", async _ => { ct.ThrowIfCancellationRequested(); return await LoadAsync(ct); });\n"
                + "await cache.GetOrCreateAsync(\"b\", async _ => await LoadAsync(request.Aborted));";

            var source = await FixAllAsync(Header + statements + Footer);

            Assert.Equal(Header
                + "await cache.GetOrCreateAsync(\"a\", async ct => { ct.ThrowIfCancellationRequested(); return await LoadAsync(ct); }, cancellationToken: ct);\n"
                + "await cache.GetOrCreateAsync(\"b\", async ct => await LoadAsync(ct), cancellationToken: request.Aborted);" + Footer, source);
            Assert.Empty(await AnalyzeAsync(source));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        {
            var compilation = Compile(source);
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new FactoryTokenAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }

        private static CSharpCompilation Compile(string source) => CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        private static async Task<(string Title, string Source)> FixAsync(string source)
        {
            var (document, diagnostics) = await DocumentAsync(source);
            var actions = new List<CodeAction>();
            await new FactoryTokenCodeFixProvider().RegisterCodeFixesAsync(
                new CodeFixContext(document, diagnostics[0], (action, _) => actions.Add(action), CancellationToken.None));
            var action = Assert.Single(actions);
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return (action.Title, (await changed.GetDocument(document.Id)!.GetTextAsync()).ToString());
        }

        private static async Task<string> FixAllAsync(string source)
        {
            var (document, diagnostics) = await DocumentAsync(source);
            var provider = new FactoryTokenCodeFixProvider();
            var fixAll = provider.GetFixAllProvider();
            Assert.NotNull(fixAll);
            var context = new FixAllContext(document, provider, FixAllScope.Document, "UseFactoryToken", ["MC0035"],
                new Diagnostics(diagnostics), CancellationToken.None);
            var action = await fixAll.GetFixAsync(context);
            var operations = await action!.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return (await changed.GetDocument(document.Id)!.GetTextAsync()).ToString();
        }

        private static async Task<(Document Document, ImmutableArray<Diagnostic> Diagnostics)> DocumentAsync(string source)
        {
            var workspace = new AdhocWorkspace();
            var document = workspace.CurrentSolution
                .AddProject("Test", "Test", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .WithMetadataReferences(References())
                .AddDocument("File.cs", SourceText.From(source));
            var compilation = (await document.Project.GetCompilationAsync())!;
            var diagnostics = (await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new FactoryTokenAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
            Assert.NotEmpty(diagnostics);
            return (document, diagnostics);
        }

        private sealed class Diagnostics(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
        {
            public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
                => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

            public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
                => Task.FromResult<IEnumerable<Diagnostic>>([]);

            public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
                => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
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
