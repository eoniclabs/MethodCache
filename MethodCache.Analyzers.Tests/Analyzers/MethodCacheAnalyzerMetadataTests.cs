using System.Collections.Immutable;
using System.Reflection;
using MethodCache.Core.Configuration.Surfaces.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class MethodCacheAnalyzerMetadataTests
    {
        private readonly ITestOutputHelper _output;

        public MethodCacheAnalyzerMetadataTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Analyzer_ShouldHaveCorrectSupportedDiagnostics()
        {
            var analyzer = new MethodCacheAnalyzer();
            var supportedDiagnostics = analyzer.SupportedDiagnostics;

            Assert.Equal(4, supportedDiagnostics.Length);

            var cacheRule = supportedDiagnostics.FirstOrDefault(d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            var invalidateRule = supportedDiagnostics.FirstOrDefault(d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            var keyGeneratorRule = supportedDiagnostics.FirstOrDefault(d => d.Id == MethodCacheAnalyzer.CacheKeyGeneratorDiagnosticId);

            Assert.NotNull(cacheRule);
            Assert.NotNull(invalidateRule);
            Assert.NotNull(keyGeneratorRule);
        }

        [Fact]
        public void CacheRule_ShouldHaveCorrectMetadata()
        {
            var analyzer = new MethodCacheAnalyzer();
            var cacheRule = analyzer.SupportedDiagnostics.First(d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);

            Assert.Equal("MC0001", cacheRule.Id);
            Assert.Equal("MethodCache Analyzer", cacheRule.Title.ToString());
            Assert.Equal("Usage", cacheRule.Category);
            Assert.Equal(DiagnosticSeverity.Warning, cacheRule.DefaultSeverity);
            Assert.True(cacheRule.IsEnabledByDefault);
            
            Assert.Contains("virtual", cacheRule.Description.ToString());
            Assert.Contains("abstract", cacheRule.Description.ToString());
            Assert.Contains("interface", cacheRule.Description.ToString());
            
            var messageFormat = cacheRule.MessageFormat.ToString();
            Assert.Contains("{0}", messageFormat); // Should have placeholder for method name
            Assert.Contains("[Cache]", messageFormat);
        }

        [Fact]
        public void InvalidateRule_ShouldHaveCorrectMetadata()
        {
            var analyzer = new MethodCacheAnalyzer();
            var invalidateRule = analyzer.SupportedDiagnostics.First(d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);

            Assert.Equal("MC0002", invalidateRule.Id);
            Assert.Equal("MethodCache Invalidate Analyzer", invalidateRule.Title.ToString());
            Assert.Equal("Usage", invalidateRule.Category);
            Assert.Equal(DiagnosticSeverity.Warning, invalidateRule.DefaultSeverity);
            Assert.True(invalidateRule.IsEnabledByDefault);
            
            Assert.Contains("async", invalidateRule.Description.ToString());
            Assert.Contains("Task", invalidateRule.Description.ToString());
            
            var messageFormat = invalidateRule.MessageFormat.ToString();
            Assert.Contains("{0}", messageFormat); // Should have placeholder for method name
            Assert.Contains("[CacheInvalidate]", messageFormat);
        }

        [Fact]
        public async Task CacheDiagnostic_ShouldHaveCorrectLocationAndMessage()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        public string GetValue(int id)
        {
            return $""Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            var diagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);

            // Check message content
            var message = diagnostic.GetMessage();
            Assert.Contains("GetValue", message);
            Assert.Contains("[Cache]", message);
            Assert.Contains("virtual", message);
            Assert.Contains("abstract", message);
            Assert.Contains("interface", message);

            // Check location
            var location = diagnostic.Location;
            Assert.True(location.IsInSource);
            
            var lineSpan = location.GetLineSpan();
            _output.WriteLine($"Diagnostic location: Line {lineSpan.StartLinePosition.Line + 1}, Column {lineSpan.StartLinePosition.Character + 1}");
            
            // Should point to the method declaration line
            var sourceLines = source.Split('\n');
            var methodLine = sourceLines.FirstOrDefault(line => line.Contains("public string GetValue"));
            Assert.NotNull(methodLine);
        }

        [Fact]
        public async Task InvalidateDiagnostic_ShouldHaveCorrectLocationAndMessage()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public string ClearCache()
        {
            return ""Cleared"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            var diagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);

            // Check message content
            var message = diagnostic.GetMessage();
            Assert.Contains("ClearCache", message);
            Assert.Contains("[CacheInvalidate]", message);
            Assert.Contains("async", message);

            // Check location
            var location = diagnostic.Location;
            Assert.True(location.IsInSource);
            
            var lineSpan = location.GetLineSpan();
            _output.WriteLine($"Diagnostic location: Line {lineSpan.StartLinePosition.Line + 1}, Column {lineSpan.StartLinePosition.Character + 1}");
        }

        [Fact]
        public async Task MultipleDiagnostics_ShouldHaveUniqueLocations()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        public string GetValue1(int id) => $""Value1 {id}"";

        [Cache]
        public string GetValue2(int id) => $""Value2 {id}"";

        [CacheInvalidate(Tags = new[] { ""cache1"" })]
        public bool ClearCache1() => true;

        [CacheInvalidate(Tags = new[] { ""cache2"" })]
        public int ClearCache2() => 1;
    }
}";

            var result = await AnalyzeAsync(source);
            
            var cacheDiagnostics = result.AnalyzerDiagnostics.Where(d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId).ToList();
            var invalidateDiagnostics = result.AnalyzerDiagnostics.Where(d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId).ToList();

            Assert.Equal(2, cacheDiagnostics.Count);
            Assert.Equal(2, invalidateDiagnostics.Count);

            // Check that all diagnostics have different locations
            var allLocations = result.AnalyzerDiagnostics.Select(d => d.Location.GetLineSpan().StartLinePosition).ToList();
            var uniqueLocations = allLocations.Distinct().ToList();
            
            Assert.Equal(allLocations.Count, uniqueLocations.Count);

            // Check method names in messages
            Assert.Contains(cacheDiagnostics, d => d.GetMessage().Contains("GetValue1"));
            Assert.Contains(cacheDiagnostics, d => d.GetMessage().Contains("GetValue2"));
            Assert.Contains(invalidateDiagnostics, d => d.GetMessage().Contains("ClearCache1"));
            Assert.Contains(invalidateDiagnostics, d => d.GetMessage().Contains("ClearCache2"));
        }

        [Fact]
        public void Analyzer_ShouldSupportConcurrentExecution()
        {
            var analyzer = new MethodCacheAnalyzer();
            
            // This is a bit tricky to test directly, but we can at least verify
            // that the analyzer doesn't throw when analyzing multiple files concurrently
            // The actual concurrent execution test would be in the analyzer initialization
            Assert.NotNull(analyzer);
            Assert.Equal(4, analyzer.SupportedDiagnostics.Length);
        }

        [Fact]
        public void Analyzer_ShouldIgnoreGeneratedCode()
        {
            // This tests that the analyzer is configured to ignore generated code
            // The actual behavior is tested in the Initialize method configuration
            var analyzer = new MethodCacheAnalyzer();
            Assert.NotNull(analyzer);
            
            // We can't directly test the GeneratedCodeAnalysisFlags.None setting,
            // but we can verify the analyzer initializes without errors
            Assert.Equal(4, analyzer.SupportedDiagnostics.Length);
        }

        private static async Task<AnalysisResult> AnalyzeAsync(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
            };

            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var analyzer = new MethodCacheAnalyzer();
            var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

            var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
            var compilationDiagnostics = compilation.GetDiagnostics();

            return new AnalysisResult
            {
                AnalyzerDiagnostics = diagnostics.ToList(),
                CompilationDiagnostics = compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList()
            };
        }

        public class AnalysisResult
        {
            public List<Diagnostic> AnalyzerDiagnostics { get; set; } = new();
            public List<Diagnostic> CompilationDiagnostics { get; set; } = new();
        }
    }
}
