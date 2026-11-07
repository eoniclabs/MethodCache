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

        [Theory]
        // An interface whose implementation in this project reads the request
        [InlineData("public interface IReports { } public sealed class Reports(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IReports { } public sealed class Prices(IReports reports) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }",
            "'reports' (IReports, and Reports's constructor takes IHttpContextAccessor)")]
        // Two constructors down: Prices takes Reports, which takes an IStats whose implementation reads the request
        [InlineData("public interface IStats { } public sealed class Stats(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IStats { } public sealed class Reports(IStats stats) { } public sealed class Prices(Reports reports) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }",
            "'reports' (Reports, and Stats's constructor takes IHttpContextAccessor)")]
        public async Task RequestStateFurtherDown_IsReported(string declaration, string taken)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + declaration));

            Assert.Equal("MC0026", diagnostic.Id);
            Assert.Contains(taken, diagnostic.GetMessage());
        }

        [Fact]
        public async Task RequestStateThreeConstructorsDown_IsNotReported()
        {
            var source = Header + "public sealed class Http(Microsoft.AspNetCore.Http.IHttpContextAccessor http) { } public sealed class Stats(Http http) { } public sealed class Reports(Stats stats) { } public sealed class Prices(Reports reports) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task TypeNamedInEditorConfig_IsRequestState()
        {
            var source = Header + "namespace App { public interface ITenant { } public interface ICurrentUser { } public sealed class Prices(ICurrentUser user) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); } }";

            var diagnostic = Assert.Single(await AnalyzeAsync(source, new Dictionary<string, string> { ["methodcache.request_state_types"] = "App.ITenant, App.ICurrentUser" }));

            Assert.Contains("'user' (ICurrentUser)", diagnostic.GetMessage());
            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task Message_PointsToTheCheckPattern()
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + "public sealed class Prices(ClaimsPrincipal user) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }"));

            Assert.Contains("[ChecksRequestState]", diagnostic.GetMessage());
        }

        // A context filtered by the request's tenant, with a check that the tenant passed in is the request's
        private const string Tenants = @"
public interface ITenants
{
    [Cache(Duration = ""5m"")]
    Task<int> CountAsync(System.Guid tenantId);

    [Cache(Duration = ""5m"")]
    Task<string> NameAsync(System.Guid tenantId, int id);

    Task SaveAsync(System.Guid tenantId);
}

public class TenantDb
{
    public TenantDb(Microsoft.AspNetCore.Http.IHttpContextAccessor http) { }

    [ChecksRequestState]
    public void EnsureActsFor(System.Guid tenantId) { }

    public void Log(System.Guid tenantId) { }
}

public interface ITenantAccess
{
    [ChecksRequestState]
    void EnsureActsFor(System.Guid tenantId);
}

public sealed class TenantAccess(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : ITenantAccess
{
    public void EnsureActsFor(System.Guid tenantId) { }
}

public static class TenantChecks
{
    [ChecksRequestState]
    public static void Check(this ClaimsPrincipal user, System.Guid tenantId) { }
}
";

        [Theory]
        // Each cached method passes its tenant parameter to the check; the method that isn't cached doesn't have to
        [InlineData("public sealed class Store(TenantDb db) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { db.EnsureActsFor(tenantId); return Task.FromResult(1); } public async Task<string> NameAsync(System.Guid tenantId, int id) { db.EnsureActsFor(tenantId); await Task.Yield(); return \"\"; } public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        // The check marked on the interface method, called through the interface
        [InlineData("public sealed class Store(ITenantAccess access) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { access.EnsureActsFor(tenantId); return Task.FromResult(1); } public Task<string> NameAsync(System.Guid tenantId, int id) { access.EnsureActsFor(tenantId); return Task.FromResult(\"\"); } public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        // An extension method of the app's own, on the request state itself
        [InlineData("public sealed class Store(ClaimsPrincipal user) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { user.Check(tenantId); return Task.FromResult(1); } public Task<string> NameAsync(System.Guid tenantId, int id) { user.Check(tenantId); return Task.FromResult(\"\"); } public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        public async Task CachedMethodsThatCheckTheirParameter_AreNotReported(string declaration)
        {
            Assert.Empty(await AnalyzeAsync(Header + Tenants + declaration));
        }

        [Theory]
        // One cached method doesn't check
        [InlineData("public sealed class Store(TenantDb db) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { db.EnsureActsFor(tenantId); return Task.FromResult(1); } public Task<string> NameAsync(System.Guid tenantId, int id) => Task.FromResult(\"\"); public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        // The check gets a value that isn't a parameter of the cached method
        [InlineData("public sealed class Store(TenantDb db) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { db.EnsureActsFor(System.Guid.Empty); return Task.FromResult(1); } public Task<string> NameAsync(System.Guid tenantId, int id) { db.EnsureActsFor(System.Guid.Empty); return Task.FromResult(\"\"); } public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        // A method without the attribute
        [InlineData("public sealed class Store(TenantDb db) : ITenants { public Task<int> CountAsync(System.Guid tenantId) { db.Log(tenantId); return Task.FromResult(1); } public Task<string> NameAsync(System.Guid tenantId, int id) { db.Log(tenantId); return Task.FromResult(\"\"); } public Task SaveAsync(System.Guid tenantId) => Task.CompletedTask; }")]
        public async Task CachedMethodsThatDontCheckTheirParameter_AreReported(string declaration)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + Tenants + declaration));

            Assert.Equal("MC0026", diagnostic.Id);
        }

        // A data project whose class reads the request behind an interface, as SpiderAds.Data's statistics store did
        private const string DataProject = @"
namespace Microsoft.AspNetCore.Http { public interface IHttpContextAccessor { } }
namespace Data
{
    public interface IStats { int Count(); }
    public interface IHeaders { string Get(string name); }
    public interface ITenantSession { }
    internal sealed class Headers(Microsoft.AspNetCore.Http.IHttpContextAccessor http) : IHeaders { public string Get(string name) => """"; }
    internal sealed class RavenStats(IHeaders headers) : IStats { public int Count() => 1; }
    public interface IPlans { int Count(); }
    internal sealed class Plans(ITenantSession session) : IPlans { public int Count() => 1; }
    public interface IClock { int Now(); }
    internal sealed class Clock(System.TimeProvider time) : IClock { public int Now() => 1; }
}
";

        private const string AppProject = @"
using System.Threading.Tasks;
using MethodCache;

public interface IPrices
{
    [Cache(Duration = ""5m"")]
    Task<decimal> GetAsync(int id);
}
";

        [Theory]
        [InlineData("public sealed class Prices(Data.IStats stats) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'stats' (IStats, and Headers's constructor takes IHttpContextAccessor)")]
        [InlineData("public sealed class Prices(Data.IHeaders headers) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'headers' (IHeaders, and Headers's constructor takes IHttpContextAccessor)")]
        [InlineData("public sealed class Prices(Data.IPlans plans) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", "'plans' (IPlans, and Plans's constructor takes ITenantSession)")]
        public async Task InterfaceOfAReferencedProject_IsFollowedToItsImplementation(string declaration, string taken)
        {
            var data = await DataReferenceAsync(generate: true);

            var diagnostic = Assert.Single(await AnalyzeAsync(AppProject + declaration, references: [data]));

            Assert.Equal("MC0026", diagnostic.Id);
            Assert.Contains(taken, diagnostic.GetMessage());
        }

        [Fact]
        public async Task InterfaceOfAReferencedProject_WithoutRequestState_IsNotReported()
        {
            var data = await DataReferenceAsync(generate: true);

            Assert.Empty(await AnalyzeAsync(AppProject + "public sealed class Prices(Data.IClock clock) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", references: [data]));
        }

        [Fact]
        public async Task InterfaceOfAReferencedProject_BuiltWithoutTheGenerator_IsNotFollowed()
        {
            var data = await DataReferenceAsync(generate: false);

            Assert.Empty(await AnalyzeAsync(AppProject + "public sealed class Prices(Data.IStats stats) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }", references: [data]));
        }

        [Fact]
        public async Task Generator_WritesTheClassesThatTakeRequestState()
        {
            var (_, generated) = await DataCompilationAsync(generate: true);

            Assert.Contains("\"MethodCache.RequestState\", \"Data.IHeaders|Headers||IHttpContextAccessor\"", generated);
            Assert.Contains("\"MethodCache.RequestState\", \"Data.IStats|RavenStats|Headers|IHttpContextAccessor\"", generated);
            Assert.Contains("\"MethodCache.RequestState\", \"Data.IPlans|Plans||ITenantSession\"", generated);
            Assert.DoesNotContain("Data.IClock", generated);
        }

        private static async Task<MetadataReference> DataReferenceAsync(bool generate)
        {
            var (compilation, _) = await DataCompilationAsync(generate);
            // A reference assembly, as a build passes a referenced project: the internal classes are gone
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: true, includePrivateMembers: false));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        private static Task<(Compilation Compilation, string Generated)> DataCompilationAsync(bool generate)
        {
            Compilation data = CSharpCompilation.Create("Data", [CSharpSyntaxTree.ParseText(DataProject)], References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            if (!generate) return Task.FromResult((data, ""));
            var options = new EditorConfig(new Dictionary<string, string> { ["methodcache.request_state_types"] = "Data.ITenantSession" });
            CSharpGeneratorDriver.Create([new SourceGenerator.MethodCacheGenerator().AsSourceGenerator()], optionsProvider: options)
                .RunGeneratorsAndUpdateCompilation(data, out data, out var diagnostics);
            Assert.Empty(diagnostics);
            Assert.Empty(data.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return Task.FromResult((data, string.Join("\n", data.SyntaxTrees.Skip(1).Select(tree => tree.ToString()))));
        }

        [Theory]
        [InlineData("App.IUser")]
        [InlineData("App.ICurrentUser, App.IGone<T>")]
        public async Task ConfiguredName_ThatMatchesNoTypeInAnExistingNamespace_IsReported(string configured)
        {
            var source = Header + "namespace App { public interface ICurrentUser { } }";

            var diagnostic = Assert.Single(await AnalyzeAsync(source, new Dictionary<string, string> { ["methodcache.request_state_types"] = configured }));

            Assert.Equal("MC0036", diagnostic.Id);
            Assert.Equal(Location.None, diagnostic.Location);
            Assert.Contains("App", diagnostic.GetMessage());
        }

        [Theory]
        // Types that exist: plain, generic, nested, and a framework type
        [InlineData("App.ICurrentUser, App.IRepository<T>, App.Outer.Inner, System.TimeProvider")]
        // A namespace this project doesn't see, as a solution-wide .editorconfig names a type of another project
        [InlineData("Web.ICurrentUser")]
        public async Task ConfiguredName_ThatMatchesATypeOrAnUnseenNamespace_IsNotReported(string configured)
        {
            var source = Header + "namespace App { public interface ICurrentUser { } public interface IRepository<T> { } public class Outer { public class Inner { } } }";

            Assert.Empty(await AnalyzeAsync(source, new Dictionary<string, string> { ["methodcache.request_state_types"] = configured }));
        }

        private sealed class EditorConfig(Dictionary<string, string> values) : AnalyzerConfigOptionsProvider
        {
            private readonly Options _options = new(values);
            public override AnalyzerConfigOptions GlobalOptions => _options;
            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;

            private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
            {
                public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
            }
        }

        private static IEnumerable<MetadataReference> References()
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            return trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, Dictionary<string, string>? editorConfig = null,
            IEnumerable<MetadataReference>? references = null)
        {
            var compilation = CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(source)], References().Concat(references ?? []),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new EditorConfig(editorConfig ?? new Dictionary<string, string>()));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RequestStateAnalyzer()), options)
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
