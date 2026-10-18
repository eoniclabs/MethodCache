using System.Collections.Immutable;
using System.Reflection;
using MethodCache;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class MethodCacheAnalyzerTests
    {
        [Fact]
        public void SupportedDiagnostics_AreTheAttributeOnClassAndTimeSpanRules()
        {
            var ids = new MethodCacheAnalyzer().SupportedDiagnostics.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal);

            Assert.Equal(new[] { "MC0001", "MC0004", "MC0021" }, ids);
        }

        [Theory]
        [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Values), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
        public async Task SharedDurationCorpus_ValidValues(string text, long ticks)
        {
            _ = ticks;
            Assert.Empty(await AnalyzeAsync("using MethodCache; public interface IReader { [Cache(Duration = \"" + text + "\")] int Read(); }"));
        }

        [Theory]
        [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Invalid), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
        public async Task SharedDurationCorpus_InvalidValues(string text)
        {
            Assert.Equal("MC0004", Assert.Single(await AnalyzeAsync("using MethodCache; public interface IReader { [Cache(Duration = \"" + text + "\")] int Read(); }")).Id);
        }

        [Fact]
        public async Task ExplicitNegativeVersion_IsAnError()
            => Assert.Equal("MC0021", Assert.Single(await AnalyzeAsync("using MethodCache; public interface IReader { [Cache(Version = -1)] int Read(); }")).Id);

        [Fact]
        public async Task ArrayValuedTags_DoNotCrashDurationValidation()
        {
            var source = """
                using MethodCache;
                [assembly: CacheInterface(typeof(IReader), "Read", Tags = new[] { "users" }, Duration = "1m")]
                public interface IReader { [Cache(Tags = new[] { "users", "{id}" }, Duration = "1m")] int Read(int id); }
                """;
            Assert.Empty(await AnalyzeAsync(source));
        }

        [Theory]
        [InlineData("public string Get(int id) => \"\";")]
        [InlineData("public virtual string Get(int id) => \"\";")]
        [InlineData("public static string Get(int id) => \"\";")]
        [InlineData("private string Get(int id) => \"\";")]
        public async Task CacheOnClassMethod_ReportsMC0001(string member)
        {
            var source = $@"
using MethodCache;

public class UserService
{{
    [Cache]
    {member}
}}";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));
            Assert.Equal(MethodCacheAnalyzer.AttributeOnClassDiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal("[Cache] on 'Get' has no effect: MethodCache caches interface methods. Put [Cache] on the interface method instead.", diagnostic.GetMessage());
            Assert.Equal("Cache", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }

        [Fact]
        public async Task CacheOnInterfaceImplementation_ReportsMC0001()
        {
            // The interface method decides; [Cache] on the implementation is ignored
            var source = @"
using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    Task<string> GetAsync(int id);
}

public class UserService : IUserService
{
    [Cache(Duration = ""00:05:00"")]
    public Task<string> GetAsync(int id) => Task.FromResult("""");
}";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));
            Assert.Equal(MethodCacheAnalyzer.AttributeOnClassDiagnosticId, diagnostic.Id);
        }

        [Theory]
        [InlineData("struct")]
        [InlineData("record")]
        [InlineData("record struct")]
        public async Task CacheInvalidateOnOtherTypes_ReportsMC0001(string kind)
        {
            var source = $@"
using MethodCache;

public {kind} Users
{{
    [CacheInvalidate(Tags = new[] {{ ""users"" }})]
    public void Clear() {{ }}
}}";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));
            Assert.Equal(MethodCacheAnalyzer.AttributeOnClassDiagnosticId, diagnostic.Id);
            Assert.StartsWith("[CacheInvalidate] on 'Clear' has no effect", diagnostic.GetMessage());
        }

        [Fact]
        public async Task AttributesOnInterfaceMethods_ReportNothing()
        {
            // Sync and non-Task methods are fine: the generator handles every return shape it accepts
            var source = @"
using System.Threading.Tasks;
using MethodCache;

public interface IUserService
{
    [Cache(Duration = ""1.00:00:00"", SlidingExpiration = ""00:05:00"", RefreshAhead = ""00:00:30"")]
    Task<string> GetAsync(int id);

    [Cache]
    string Get(int id);

    [CacheInvalidate(Tags = new[] { ""users"" })]
    bool Update(int id);

    [CacheInvalidate(Tags = new[] { ""users"" })]
    ValueTask UpdateAsync(int id);
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task OtherAttributesNamedCache_AreIgnored()
        {
            var source = @"
namespace Other
{
    public sealed class CacheAttribute : System.Attribute { }

    public class Service
    {
        [Cache]
        public string Get() => """";
    }
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task GeneratedCode_IsIgnored()
        {
            var source = @"
// <auto-generated/>
using MethodCache;

public class Generated
{
    [Cache]
    public string Get() => """";
}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Theory]
        [InlineData("Duration", "5 minutes")]
        [InlineData("SlidingExpiration", "00:00:00")]
        [InlineData("RefreshAhead", "-00:01:00")]
        [InlineData("FailSafe", "1 hour")]
        [InlineData("FailSafeTimeout", "00:00:00")]
        public async Task CacheWithInvalidTimeSpan_ReportsMC0004(string property, string value)
        {
            var source = $@"
using MethodCache;

public interface IService
{{
    [Cache({property} = ""{value}"")]
    string Get(int id);
}}";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));
            Assert.Equal(MethodCacheAnalyzer.InvalidTimeSpanDiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal($"{property} = \"{value}\" is not a positive duration; use a value such as \"5m\" or \"00:05:00\"", diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("off")]
        [InlineData("OFF")]
        [InlineData("01:00:00")]
        public async Task CacheWithValidFailSafe_ReportsNothing(string value)
        {
            var source = $@"
using MethodCache;

public interface IService
{{
    [Cache(FailSafe = ""{value}"", FailSafeTimeout = ""00:00:00.200"")]
    string Get(int id);
}}";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Theory]
        [InlineData("Duration", "5 minutes")]
        [InlineData("RefreshAhead", "00:00:00")]
        public async Task CacheInterfaceWithInvalidTimeSpan_ReportsMC0004(string property, string value)
        {
            var source = $@"
using MethodCache;

[assembly: System.CLSCompliant(false)]
[assembly: CacheInterface(typeof(System.IFormatProvider), ""GetFormat"", {property} = ""{value}"")]
[assembly: CacheInterface(typeof(System.ICloneable), ""Clone"", Duration = ""00:05:00"")]";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));
            Assert.Equal(MethodCacheAnalyzer.InvalidTimeSpanDiagnosticId, diagnostic.Id);
            Assert.Contains("IFormatProvider", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }

        [Fact]
        public async Task CacheInterfaceWithInvalidTimeSpan_IsReportedWhileEditingTheFile()
        {
            // Reported from the file being edited, not only at the end of a full build (RS1037)
            var source = @"
using MethodCache;

[assembly: CacheInterface(typeof(System.IFormatProvider), ""GetFormat"", Duration = ""soon"")]";

            var compilation = CreateCompilation(source);
            var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MethodCacheAnalyzer()));
            var tree = compilation.SyntaxTrees.Single();

            var diagnostics = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(tree), filterSpan: null, CancellationToken.None);

            Assert.Equal(MethodCacheAnalyzer.InvalidTimeSpanDiagnosticId, Assert.Single(diagnostics).Id);
        }

        [Theory]
        [InlineData("Duration = \"0s\"", "MC0004")]
        [InlineData("Version = -1", "MC0021")]
        public async Task GeneratorAndAnalyzer_ReportInvalidSettingOnce(string setting, string id)
        {
            var input = CreateCompilation("using MethodCache; public interface IReader { [Cache(" + setting + ")] int Read(); }");
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new SourceGenerator.MethodCacheGenerator());
            driver.RunGeneratorsAndUpdateCompilation(input, out var generated, out var generatorDiagnostics);
            var analyzerDiagnostics = await generated.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new MethodCacheAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.Single(generatorDiagnostics.Concat(analyzerDiagnostics), diagnostic => diagnostic.Id == id);
        }

        private static CSharpCompilation CreateCompilation(string source)
        {
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
            };

            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return compilation;
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        {
            var compilation = CreateCompilation(source);
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MethodCacheAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
