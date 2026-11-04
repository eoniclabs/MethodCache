using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class RequestStateAnalyzerTests
    {
        private const string Header = @"using System.Security.Claims;
using System.Threading.Tasks;
using MethodCache;

namespace Microsoft.AspNetCore.Http { public interface IHttpContextAccessor { } public abstract class HttpContext { } }

public interface IPrices
{
    [Cache(Duration = ""00:05:00"")]
    Task<decimal> GetAsync(int id);
}

public interface IPlain
{
    Task<decimal> GetAsync(int id);
}
";

        [Theory]
        [InlineData("public sealed class Prices(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "IHttpContextAccessor")]
        [InlineData("public sealed class Prices : IPrices { public Prices(ClaimsPrincipal user) { } public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "ClaimsPrincipal")]
        [InlineData("public class Prices(int scale, Microsoft.AspNetCore.Http.HttpContext context) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "HttpContext")]
        public async Task CachedImplementation_TakingRequestState_IsReported(string declaration, string typeName)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + declaration));

            Assert.Equal("MC0026", diagnostic.Id);
            Assert.Contains(typeName, diagnostic.GetMessage());
            Assert.Contains("'IPrices'", diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("public sealed class Prices(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IPlain { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        [InlineData("public sealed class Prices(System.TimeProvider time) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        [InlineData("public abstract class PricesBase(ClaimsPrincipal user) : IPrices { public abstract Task<decimal> GetAsync(int id); }")]
        public async Task OtherClasses_AreNotReported(string declaration)
        {
            Assert.Empty(await AnalyzeAsync(Header + declaration));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
            var compilation = CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RequestStateAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
