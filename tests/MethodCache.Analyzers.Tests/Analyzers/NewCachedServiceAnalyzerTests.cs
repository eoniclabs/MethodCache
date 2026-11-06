using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class NewCachedServiceAnalyzerTests
    {
        private const string Header = @"using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public interface IPrices { [Cache] Task<decimal> GetAsync(int id); }
public interface IClock { System.DateTime Now(); }
public class Prices : IPrices { public Prices(int region = 0) { } public Task<decimal> GetAsync(int id) => Task.FromResult(1m); }
public sealed class EuPrices : Prices { }
public sealed class Clock : IClock { public System.DateTime Now() => System.DateTime.UtcNow; }
public sealed class Report(IPrices prices) { public IPrices Prices => prices; }

public static class Setup
{
    public static void Run(IServiceCollection services)
    {
";

        private const string Footer = @"
    }
}";

        [Theory]
        [InlineData("var prices = new Prices();", "'Prices' implements the cached interface 'IPrices'")]
        [InlineData("IPrices prices = new EuPrices();", "'EuPrices' implements the cached interface 'IPrices'")]
        [InlineData("services.AddSingleton(sp => new Report(new Prices()));", "'Prices' implements the cached interface 'IPrices'")]
        public async Task CachedServiceCreatedWithNew_IsReported(string code, string message)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + code + Footer));

            Assert.Equal("MC0032", diagnostic.Id);
            Assert.Contains(message, diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("services.AddSingleton<IPrices>(new Prices());")]
        [InlineData("services.AddSingleton<IPrices>(sp => new Prices(1));")]
        [InlineData("services.AddScoped<IPrices>(sp => { var prices = new Prices(2); return prices; });")]
        [InlineData("services.AddKeyedSingleton<IPrices>(\"eu\", new EuPrices());")]
        [InlineData("services.TryAddSingleton<IPrices>(new Prices());")]
        [InlineData("services.Add(new ServiceDescriptor(typeof(IPrices), new Prices()));")]
        [InlineData("var clock = new Clock();")]
        public async Task RegistrationsAndUncachedTypes_AreNotReported(string code)
        {
            Assert.Empty(await AnalyzeAsync(Header + code + Footer));
        }

        [Fact]
        public async Task TestProject_IsNotReported()
        {
            Assert.Empty(await AnalyzeAsync(Header + "var prices = new Prices();" + Footer, "xunit.core"));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, string? reference = null)
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location))
                .ToList();
            if (reference != null)
            {
                var library = CSharpCompilation.Create(reference, [CSharpSyntaxTree.ParseText("public class Marker { }")], references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                using var image = new MemoryStream();
                Assert.True(library.Emit(image).Success);
                references.Add(MetadataReference.CreateFromImage(image.ToArray()));
            }

            var compilation = CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NewCachedServiceAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
