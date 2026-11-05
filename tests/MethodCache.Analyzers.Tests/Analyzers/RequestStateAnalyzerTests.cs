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
    [Cache(Duration = ""5m"")]
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
        [InlineData("public sealed class Prices(UserDb db) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'db' (UserDb, whose constructor takes IHttpContextAccessor)")]
        [InlineData("public sealed class Prices(IDbFactory<UserDb> factory) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'factory' (IDbFactory<UserDb>, and UserDb's constructor takes IHttpContextAccessor)")]
        [InlineData("public sealed class Prices(PrincipalDb db) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'db' (PrincipalDb, whose constructor takes ClaimsPrincipal)")]
        public async Task CachedImplementation_TakingAClassThatTakesRequestState_IsReported(string declaration, string taken)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + Dependencies + declaration));

            Assert.Equal("MC0026", diagnostic.Id);
            Assert.Contains(taken, diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("public sealed class Prices(SharedDb db) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        [InlineData("public sealed class Prices(IDbFactory<SharedDb> factory) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        [InlineData("public sealed class Prices(HiddenDb db) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        [InlineData("public sealed class Prices(UserDb db) : IPlain { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }")]
        public async Task CachedImplementation_TakingAClassWithoutRequestState_IsNotReported(string declaration)
        {
            Assert.Empty(await AnalyzeAsync(Header + Dependencies + declaration));
        }

        // A context that filters rows by the current user, one that doesn't, and one whose only such constructor is private
        private const string Dependencies = @"
public class UserDb { public UserDb(Microsoft.AspNetCore.Http.IHttpContextAccessor http) { } }
public class PrincipalDb { public PrincipalDb(int pool, ClaimsPrincipal user) { } }
public class SharedDb { public SharedDb(System.TimeProvider time) { } }
public class HiddenDb { public HiddenDb() { } private HiddenDb(ClaimsPrincipal user) { } }
public interface IDbFactory<T> { T Create(); }
";

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
