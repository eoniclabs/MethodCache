using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class ResultTypeMismatchAnalyzerTests
    {
        private const string Header = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

public interface ITokens { [Cache] Task<int> CountAsync(int id); [Cache] Task<List<string>> NamesAsync(); }

public static class Setup
{
    public static async Task Run(MethodCacheBuilder mc, ICacheManager cache)
    {
";

        private const string Footer = @"
    }
}";

        [Theory]
        [InlineData("mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.CacheWhen<string>(s => s.Length > 0));", "CacheWhen<string> can't check the int result", "Use CacheWhen<int>")]
        [InlineData("mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.WithDuration(TimeSpan.FromMinutes(1)).DurationFor<long?>(_ => TimeSpan.FromMinutes(1)));", "DurationFor<long?> can't check the int result", "Use DurationFor<int>")]
        [InlineData("await cache.GetOrCreateAsync<int>(\"key\", _ => new ValueTask<int>(1), o => o.CacheWhen<string>(s => s != null));", "CacheWhen<string> can't check the int result", "")]
        [InlineData("await cache.GetOrCreateManyAsync<int, string>(new[] { 1 }, id => $\"k:{id}\", (ids, _) => default, o => o.CacheWhen<int>(n => n > 0));", "CacheWhen<int> can't check the string result", "")]
        [InlineData("await cache.SetAsync(\"key\", 3, o => o.CacheWhen<string>(s => s.Length > 0));", "CacheWhen<string> can't check the int result", "")]
        [InlineData("await cache.GetOrCreateAsync<int>(\"key\", _ => new ValueTask<int>(1), o => o.TagsFor<string>(s => new[] { s }));", "TagsFor<string> can't check the int result", "")]
        [InlineData("mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.SlidingFor<string>(_ => TimeSpan.FromMinutes(1)));", "SlidingFor<string> can't check the int result", "Use SlidingFor<int>")]
        public async Task OptionsForAnotherResultType_AreReported(string call, string message, string fix)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + call + Footer));

            Assert.Equal("MC0033", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains(message, diagnostic.GetMessage());
            Assert.Contains(fix, diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.CacheWhen<int>(n => n > 0));")]
        [InlineData("mc.Method((ITokens t) => t.CountAsync(0)).Configure(o => o.CacheWhen<int?>(n => n > 0).CacheWhen<object>(n => n != null));")]
        [InlineData("mc.Method((ITokens t) => t.NamesAsync()).Configure(o => o.CacheWhen<IReadOnlyList<string>>(n => n.Count > 0));")]
        [InlineData("mc.Method((ITokens t) => t.NamesAsync()).Configure(o => o.DurationFor<IEnumerable<object>>(_ => TimeSpan.FromMinutes(1)));")]
        [InlineData("await cache.GetOrCreateAsync<object>(\"key\", _ => new ValueTask<object>(1), o => o.CacheWhen<string>(s => s != null));")]
        [InlineData("var options = new CacheEntryOptions.Builder().CacheWhen<string>(s => s != null).Build();")]
        public async Task OptionsTheResultCanBe_AreNotReported(string call)
        {
            Assert.Empty(await AnalyzeAsync(Header + call + Footer));
        }

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));
            var compilation = CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ResultTypeMismatchAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
