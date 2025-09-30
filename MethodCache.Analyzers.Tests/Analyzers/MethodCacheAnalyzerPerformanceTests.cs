using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using MethodCache.Analyzers;

namespace MethodCache.Analyzers.Tests
{
    public class MethodCacheAnalyzerPerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public MethodCacheAnalyzerPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static async Task<AnalysisResult> AnalyzeAsync(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(MethodCache.Core.CacheAttribute).Assembly.Location),
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

            var stopwatch = Stopwatch.StartNew();
            var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
            stopwatch.Stop();

            var compilationDiagnostics = compilation.GetDiagnostics();

            return new AnalysisResult
            {
                AnalyzerDiagnostics = diagnostics.ToList(),
                CompilationDiagnostics = compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList(),
                AnalysisTime = stopwatch.Elapsed
            };
        }

        public class AnalysisResult
        {
            public List<Diagnostic> AnalyzerDiagnostics { get; set; } = new();
            public List<Diagnostic> CompilationDiagnostics { get; set; } = new();
            public TimeSpan AnalysisTime { get; set; }
        }

        [Fact]
        public async Task LargeCodebase_ShouldAnalyzeCorrectly()
        {
            // Generate a large codebase with many classes and methods to test analyzer correctness at scale
            var sourceBuilder = new StringBuilder();
            sourceBuilder.AppendLine("using MethodCache.Core;");
            sourceBuilder.AppendLine("using System.Threading.Tasks;");
            sourceBuilder.AppendLine();
            sourceBuilder.AppendLine("namespace TestApp");
            sourceBuilder.AppendLine("{");

            // Generate 50 classes with 10 methods each (500 methods total)
            for (int classIndex = 0; classIndex < 50; classIndex++)
            {
                sourceBuilder.AppendLine($"    public class TestService{classIndex}");
                sourceBuilder.AppendLine("    {");

                for (int methodIndex = 0; methodIndex < 10; methodIndex++)
                {
                    // Mix of different method types
                    if (methodIndex % 4 == 0)
                    {
                        // Virtual method with cache (should not report diagnostic)
                        sourceBuilder.AppendLine($"        [Cache]");
                        sourceBuilder.AppendLine($"        public virtual string GetValue{methodIndex}(int id) => $\"Value {{id}}\";");
                    }
                    else if (methodIndex % 4 == 1)
                    {
                        // Non-virtual method with cache (should report diagnostic)
                        sourceBuilder.AppendLine($"        [Cache]");
                        sourceBuilder.AppendLine($"        public string GetValue{methodIndex}(int id) => $\"Value {{id}}\";");
                    }
                    else if (methodIndex % 4 == 2)
                    {
                        // Async method with invalidate (should not report diagnostic)
                        sourceBuilder.AppendLine($"        [CacheInvalidate(Tags = new[] {{ \"tag{methodIndex}\" }})]");
                        sourceBuilder.AppendLine($"        public async Task ClearCache{methodIndex}() => await Task.Delay(1);");
                    }
                    else
                    {
                        // Sync non-void method with invalidate (should report diagnostic)
                        sourceBuilder.AppendLine($"        [CacheInvalidate(Tags = new[] {{ \"tag{methodIndex}\" }})]");
                        sourceBuilder.AppendLine($"        public bool ClearCache{methodIndex}() => true;");
                    }
                    sourceBuilder.AppendLine();
                }

                sourceBuilder.AppendLine("    }");
                sourceBuilder.AppendLine();
            }

            sourceBuilder.AppendLine("}");

            var source = sourceBuilder.ToString();
            _output.WriteLine($"Generated source code with {source.Split('\n').Length} lines");

            var result = await AnalyzeAsync(source);

            _output.WriteLine($"Analysis completed in {result.AnalysisTime.TotalMilliseconds:F2} ms");
            _output.WriteLine($"Found {result.AnalyzerDiagnostics.Count} analyzer diagnostics");

            // Correctness assertion: should find expected number of diagnostics
            // 50 classes * 10 methods per class = 500 methods total
            // methodIndex % 4 == 1: non-virtual cache method (should report diagnostic) = 50 * 3 = 150
            // methodIndex % 4 == 3: sync invalidate method (should report diagnostic) = 50 * 2 = 100
            var expectedCacheDiagnostics = 50 * 3; // methodIndex 1, 5, 9 per class
            var expectedInvalidateDiagnostics = 50 * 2; // methodIndex 3, 7 per class

            var cacheDiagnostics = result.AnalyzerDiagnostics.Count(d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            var invalidateDiagnostics = result.AnalyzerDiagnostics.Count(d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);

            _output.WriteLine($"Cache diagnostics: {cacheDiagnostics} (expected: {expectedCacheDiagnostics})");
            _output.WriteLine($"Invalidate diagnostics: {invalidateDiagnostics} (expected: {expectedInvalidateDiagnostics})");

            Assert.Equal(expectedCacheDiagnostics, cacheDiagnostics);
            Assert.Equal(expectedInvalidateDiagnostics, invalidateDiagnostics);
        }

        [Fact]
        public async Task DeepInheritanceHierarchy_ShouldAnalyzeCorrectly()
        {
            var sourceBuilder = new StringBuilder();
            sourceBuilder.AppendLine("using MethodCache.Core;");
            sourceBuilder.AppendLine();
            sourceBuilder.AppendLine("namespace TestApp");
            sourceBuilder.AppendLine("{");

            // Create a deep inheritance hierarchy
            sourceBuilder.AppendLine("    public abstract class BaseService");
            sourceBuilder.AppendLine("    {");
            sourceBuilder.AppendLine("        public abstract string GetValue(int id);");
            sourceBuilder.AppendLine("    }");
            sourceBuilder.AppendLine();

            for (int level = 1; level <= 10; level++)
            {
                var baseClass = level == 1 ? "BaseService" : $"Level{level - 1}Service";
                sourceBuilder.AppendLine($"    public class Level{level}Service : {baseClass}");
                sourceBuilder.AppendLine("    {");
                sourceBuilder.AppendLine("        [Cache]");
                sourceBuilder.AppendLine($"        public override string GetValue(int id) => $\"Level{level} Value {{id}}\";");
                sourceBuilder.AppendLine();
                sourceBuilder.AppendLine("        [Cache]");
                sourceBuilder.AppendLine($"        public virtual string GetLevel{level}Value(int id) => $\"Level{level} Specific {{id}}\";");
                sourceBuilder.AppendLine("    }");
                sourceBuilder.AppendLine();
            }

            sourceBuilder.AppendLine("}");

            var source = sourceBuilder.ToString();
            var result = await AnalyzeAsync(source);

            _output.WriteLine($"Deep inheritance analysis completed in {result.AnalysisTime.TotalMilliseconds:F2} ms");
            _output.WriteLine($"Found {result.AnalyzerDiagnostics.Count} analyzer diagnostics");

            Assert.Empty(result.CompilationDiagnostics);
            
            // All methods should be valid (override or virtual), so no diagnostics expected
            Assert.Empty(result.AnalyzerDiagnostics);
        }

        [Fact]
        public async Task ComplexInterfaceImplementations_ShouldAnalyzeCorrectly()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public interface IService1
    {
        string GetValue1(int id);
        Task ClearCache1();
    }

    public interface IService2
    {
        string GetValue2(int id);
        bool ClearCache2();
    }

    public interface IService3 : IService1, IService2
    {
        string GetValue3(int id);
        void ClearCache3();
    }

    public class MultiInterfaceService : IService3
    {
        [Cache]
        public string GetValue1(int id) => $""Value1 {id}"";

        [Cache]
        public string GetValue2(int id) => $""Value2 {id}"";

        [Cache]
        public string GetValue3(int id) => $""Value3 {id}"";

        [CacheInvalidate(Tags = new[] { ""cache1"" })]
        public Task ClearCache1() => Task.CompletedTask;

        [CacheInvalidate(Tags = new[] { ""cache2"" })]
        public bool ClearCache2() => true;

        [CacheInvalidate(Tags = new[] { ""cache3"" })]
        public void ClearCache3() { }

        // Additional non-interface methods
        [Cache]
        public string GetNonInterfaceValue(int id) => $""Non-interface {id}"";

        [Cache]
        public virtual string GetVirtualValue(int id) => $""Virtual {id}"";
    }
}";

            var result = await AnalyzeAsync(source);

            _output.WriteLine($"Complex interface analysis completed in {result.AnalysisTime.TotalMilliseconds:F2} ms");
            _output.WriteLine($"Found {result.AnalyzerDiagnostics.Count} analyzer diagnostics");

            Assert.Empty(result.CompilationDiagnostics);

            // Should have 2 diagnostics:
            // 1. GetNonInterfaceValue (non-virtual, non-interface method with Cache)
            // 2. ClearCache2 (non-void, non-async method with CacheInvalidate)
            Assert.Equal(2, result.AnalyzerDiagnostics.Count);

            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);

            Assert.Contains("GetNonInterfaceValue", cacheDiagnostic.GetMessage());
            Assert.Contains("ClearCache2", invalidateDiagnostic.GetMessage());
        }

        [Fact]
        public async Task EmptyCodebase_ShouldNotProduceDiagnostics()
        {
            var source = @"
namespace TestApp
{
    public class EmptyService
    {
        public void DoNothing() { }
    }
}";

            var result = await AnalyzeAsync(source);

            _output.WriteLine($"Empty codebase analysis completed in {result.AnalysisTime.TotalMilliseconds:F2} ms");

            Assert.Empty(result.CompilationDiagnostics);
            Assert.Empty(result.AnalyzerDiagnostics);
        }

        [Fact]
        public async Task ManyAttributesOnSingleMethod_ShouldAnalyzeCorrectly()
        {
            var source = @"
using MethodCache.Core;
using System;
using System.ComponentModel;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        [CacheInvalidate(Tags = new[] { ""test"" })]
        [Obsolete(""This method is obsolete"")]
        [Description(""Test method with many attributes"")]
        public string TestMethod(int id)
        {
            return $""Test {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);

            _output.WriteLine($"Many attributes analysis completed in {result.AnalysisTime.TotalMilliseconds:F2} ms");
            _output.WriteLine($"Found {result.AnalyzerDiagnostics.Count} analyzer diagnostics");

            Assert.Empty(result.CompilationDiagnostics);

            // Should report both cache and invalidate diagnostics
            Assert.Equal(2, result.AnalyzerDiagnostics.Count);
            Assert.Contains(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }
    }
}
