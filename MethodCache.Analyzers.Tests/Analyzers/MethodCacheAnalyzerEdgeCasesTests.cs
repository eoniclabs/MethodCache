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
    public class MethodCacheAnalyzerEdgeCasesTests
    {
        private readonly ITestOutputHelper _output;

        public MethodCacheAnalyzerEdgeCasesTests(ITestOutputHelper output)
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

        private void LogDiagnostics(AnalysisResult result)
        {
            _output.WriteLine("=== COMPILATION DIAGNOSTICS ===");
            foreach (var diagnostic in result.CompilationDiagnostics)
            {
                _output.WriteLine($"{diagnostic.Severity}: {diagnostic.GetMessage()} at {diagnostic.Location}");
            }

            _output.WriteLine("=== ANALYZER DIAGNOSTICS ===");
            foreach (var diagnostic in result.AnalyzerDiagnostics)
            {
                _output.WriteLine($"{diagnostic.Id}: {diagnostic.GetMessage()} at {diagnostic.Location}");
            }
        }

        public class AnalysisResult
        {
            public List<Diagnostic> AnalyzerDiagnostics { get; set; } = new();
            public List<Diagnostic> CompilationDiagnostics { get; set; } = new();
        }

        #region Interface Implementation Edge Cases

        [Fact]
        public async Task ExplicitInterfaceImplementation_WithCacheAttribute_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public interface ITestService
    {
        string GetValue(int id);
    }

    public class TestService : ITestService
    {
        [Cache]
        string ITestService.GetValue(int id)
        {
            return $""Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task MultipleInterfaceImplementation_WithCacheAttribute_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public interface IService1
    {
        string GetValue(int id);
    }

    public interface IService2
    {
        string GetData(int id);
    }

    public class TestService : IService1, IService2
    {
        [Cache]
        public string GetValue(int id)
        {
            return $""Value {id}"";
        }

        [Cache]
        public string GetData(int id)
        {
            return $""Data {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task InheritedInterfaceImplementation_WithCacheAttribute_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public interface IBaseService
    {
        string GetValue(int id);
    }

    public interface IExtendedService : IBaseService
    {
        string GetExtendedValue(int id);
    }

    public class TestService : IExtendedService
    {
        [Cache]
        public string GetValue(int id)
        {
            return $""Value {id}"";
        }

        [Cache]
        public string GetExtendedValue(int id)
        {
            return $""Extended Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        #endregion

        #region Inheritance Edge Cases

        [Fact]
        public async Task VirtualMethodInBaseClass_OverriddenWithCache_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class BaseService
    {
        public virtual string GetValue(int id)
        {
            return $""Base Value {id}"";
        }
    }

    public class DerivedService : BaseService
    {
        [Cache]
        public override string GetValue(int id)
        {
            return $""Derived Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task AbstractMethodInBaseClass_ImplementedWithCache_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public abstract class BaseService
    {
        public abstract string GetValue(int id);
    }

    public class ConcreteService : BaseService
    {
        [Cache]
        public override string GetValue(int id)
        {
            return $""Concrete Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task NewMethodHidingBaseMethod_WithCache_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class BaseService
    {
        public virtual string GetValue(int id)
        {
            return $""Base Value {id}"";
        }
    }

    public class DerivedService : BaseService
    {
        [Cache]
        public new string GetValue(int id)
        {
            return $""New Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("GetValue", cacheDiagnostic.GetMessage());
        }

        #endregion

        #region Async Method Edge Cases

        [Fact]
        public async Task TaskReturningMethod_WithoutAsync_ShouldNotReportInvalidateDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public Task ClearCacheAsync()
        {
            return Task.CompletedTask;
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        [Fact]
        public async Task CustomTaskLikeType_ShouldReportInvalidateDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class CustomTask
    {
        public void Wait() { }
    }

    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public CustomTask ClearCacheAsync()
        {
            return new CustomTask();
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            Assert.Contains("ClearCacheAsync", invalidateDiagnostic.GetMessage());
        }

        #endregion

        #region Attribute Variations

        [Fact]
        public async Task CacheAttributeWithParameters_ShouldFollowSameRules()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache(RequireIdempotent = true)]
        public string GetValue(int id)
        {
            return $""Value {id}"";
        }

        [Cache(RequireIdempotent = true)]
        public virtual string GetVirtualValue(int id)
        {
            return $""Virtual Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("GetValue", cacheDiagnostic.GetMessage());
        }

        [Fact]
        public async Task CacheInvalidateAttributeWithMultipleTags_ShouldFollowSameRules()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"", ""data"", ""cache"" })]
        public string ClearMultipleCache()
        {
            return ""Cleared"";
        }

        [CacheInvalidate(Tags = new[] { ""user"", ""data"", ""cache"" })]
        public void ClearMultipleCacheVoid()
        {
            // Clear logic
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            Assert.Contains("ClearMultipleCache", invalidateDiagnostic.GetMessage());
        }

        #endregion
    }
}
