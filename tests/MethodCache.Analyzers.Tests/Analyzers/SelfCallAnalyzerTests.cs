using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class SelfCallAnalyzerTests
    {
        private const string Header = @"using System.Threading.Tasks;
using MethodCache;

public interface IPrices
{
    [Cache(Duration = ""5m"")]
    Task<decimal> GetAsync(int id);

    Task<decimal> TotalAsync(int[] ids);
}

public interface IRepo<T> { [Cache] Task<T> FindAsync(int id); Task<T[]> AllAsync(); }
";

        [Theory]
        [InlineData("public async Task<decimal> TotalAsync(int[] ids) { var sum = 0m; foreach (var id in ids) sum += await GetAsync(id); return sum; }")]
        [InlineData("public async Task<decimal> TotalAsync(int[] ids) => await this.GetAsync(ids[0]);")]
        [InlineData("public async Task<decimal> TotalAsync(int[] ids) => await ((IPrices)this).GetAsync(ids[0]);")]
        [InlineData("public Task<decimal> TotalAsync(int[] ids) { System.Func<Task<decimal>> load = () => GetAsync(1); return load(); }")]
        public async Task CallToOwnCachedMethod_IsReported(string total)
        {
            var source = Header + "public sealed class Prices : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); " + total + " }";

            var diagnostic = Assert.Single(await AnalyzeAsync(source));

            Assert.Equal("MC0031", diagnostic.Id);
            Assert.Contains("'Prices' calls its own cached method 'IPrices.GetAsync'", diagnostic.GetMessage());
        }

        [Fact]
        public async Task CallFromADerivedClassAndAGenericImplementation_IsReported()
        {
            var source = Header + @"
public abstract class PricesBase : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); public abstract Task<decimal> TotalAsync(int[] ids); }
public sealed class Prices : PricesBase { public override Task<decimal> TotalAsync(int[] ids) => GetAsync(ids[0]); }
public sealed class Repo : IRepo<string> { public Task<string> FindAsync(int id) => Task.FromResult(""""); public async Task<string[]> AllAsync() => new[] { await FindAsync(1) }; }";

            var diagnostics = await AnalyzeAsync(source);

            Assert.Equal(2, diagnostics.Length);
            Assert.All(diagnostics, d => Assert.Equal("MC0031", d.Id));
            Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Repo' calls its own cached method 'IRepo.FindAsync'"));
        }

        [Theory]
        [InlineData("public Task<decimal> TotalAsync(int[] ids) => _other.GetAsync(ids[0]);")]
        [InlineData("public Task<decimal> TotalAsync(int[] ids) => Helper(ids[0]); private Task<decimal> Helper(int id) => Task.FromResult(2m);")]
        [InlineData("public Task<decimal> TotalAsync(int[] ids) => Task.FromResult(this.GetHashCode() + 0m);")]
        public async Task OtherCalls_AreNotReported(string total)
        {
            var source = Header + "public sealed class Prices(IPrices _other) : IPrices { public Task<decimal> GetAsync(int id) => Task.FromResult(1m); " + total + " }";

            Assert.Empty(await AnalyzeAsync(source));
        }

        [Fact]
        public async Task CallToAnUncachedOwnMethod_IsNotReported()
        {
            var source = Header + "public sealed class Prices : IPrices { public Task<decimal> GetAsync(int id) => TotalAsync([id]); public Task<decimal> TotalAsync(int[] ids) => Task.FromResult(1m); }";

            Assert.Empty(await AnalyzeAsync(source));
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
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SelfCallAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
