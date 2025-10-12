using System.Collections.Immutable;
using System.Reflection;
using MethodCache.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class MethodCacheAnalyzerTests
    {
        private readonly ITestOutputHelper _output;

        public MethodCacheAnalyzerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task CacheAttribute_WithInvalidKeyGeneratorType_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class InvalidKeyGenerator {}

    public interface IService
    {
        [Cache(KeyGeneratorType = typeof(InvalidKeyGenerator))]
        string Get(int id);
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Contains(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheKeyGeneratorDiagnosticId);
        }

        [Fact]
        public async Task CacheAttribute_WithValidKeyGeneratorType_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Core.Runtime.Core;

namespace TestApp
{
    public class ValidKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy) => methodName;
    }

    public interface IService
    {
        [Cache(KeyGeneratorType = typeof(ValidKeyGenerator))]
        string Get(int id);
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheKeyGeneratorDiagnosticId);
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

        #region Cache Attribute Tests

        [Fact]
        public async Task CacheAttribute_OnVirtualMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        public virtual string GetValue(int id)
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
        public async Task CacheAttribute_OnAbstractMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public abstract class TestService
    {
        [Cache]
        public abstract string GetValue(int id);
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task CacheAttribute_OnInterfaceMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task CacheAttribute_OnInterfaceImplementation_ShouldNotReportDiagnostic()
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
        public string GetValue(int id)
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
        public async Task CacheAttribute_OnOverrideMethod_ShouldNotReportDiagnostic()
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

    public class TestService : BaseService
    {
        [Cache]
        public override string GetValue(int id)
        {
            return $""Override Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
        }

        [Fact]
        public async Task CacheAttribute_OnNonVirtualMethod_ShouldReportDiagnostic()
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
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("GetValue", cacheDiagnostic.GetMessage());
            Assert.Equal(DiagnosticSeverity.Warning, cacheDiagnostic.Severity);
        }

        [Fact]
        public async Task CacheAttribute_OnStaticMethod_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        public static string GetValue(int id)
        {
            return $""Value {id}"";
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
        public async Task CacheAttribute_OnPrivateMethod_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        private string GetValue(int id)
        {
            return $""Value {id}"";
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

        #region Cache Invalidate Attribute Tests

        [Fact]
        public async Task CacheInvalidateAttribute_OnAsyncTaskMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public async Task ClearUserCacheAsync()
        {
            await Task.Delay(100);
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnAsyncTaskTMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public async Task<bool> ClearUserCacheAsync()
        {
            await Task.Delay(100);
            return true;
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnVoidMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public void ClearUserCache()
        {
            // Clear cache logic
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnSyncNonVoidMethod_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public bool ClearUserCache()
        {
            // Clear cache logic
            return true;
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            Assert.Contains("ClearUserCache", invalidateDiagnostic.GetMessage());
            Assert.Equal(DiagnosticSeverity.Warning, invalidateDiagnostic.Severity);
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnSyncStringMethod_ShouldReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public string ClearUserCache()
        {
            // Clear cache logic
            return ""Cache cleared"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            Assert.Contains("ClearUserCache", invalidateDiagnostic.GetMessage());
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnValueTaskMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public ValueTask ClearUserCacheAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        [Fact]
        public async Task CacheInvalidateAttribute_OnValueTaskTMethod_ShouldNotReportDiagnostic()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class TestService
    {
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public ValueTask<int> ClearUserCacheAsync()
        {
            return ValueTask.FromResult(1);
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.DoesNotContain(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
        }

        #endregion

        #region Complex Scenarios

        [Fact]
        public async Task MultipleAttributes_OnSameMethod_ShouldReportBothDiagnostics()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        [CacheInvalidate(Tags = new[] { ""user"" })]
        public string ProcessData(int id)
        {
            return $""Processed {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);

            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("ProcessData", cacheDiagnostic.GetMessage());

            var invalidateDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId);
            Assert.Contains("ProcessData", invalidateDiagnostic.GetMessage());
        }

        [Fact]
        public async Task MultipleClasses_WithDifferentScenarios_ShouldReportCorrectDiagnostics()
        {
            var source = @"
using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public class GoodService
    {
        [Cache]
        public virtual string GetValue(int id) => $""Value {id}"";

        [CacheInvalidate(Tags = new[] { ""user"" })]
        public async Task ClearCacheAsync() => await Task.Delay(1);
    }

    public class BadService
    {
        [Cache]
        public string GetValue(int id) => $""Value {id}"";

        [CacheInvalidate(Tags = new[] { ""user"" })]
        public string ClearCache() => ""Cleared"";
    }

    public interface IGoodInterface
    {
        [Cache]
        string GetValue(int id);
    }

    public class InterfaceImplementation : IGoodInterface
    {
        [Cache]
        public string GetValue(int id) => $""Value {id}"";
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);

            // Should have 2 diagnostics: one for BadService.GetValue (cache) and one for BadService.ClearCache (invalidate)
            Assert.Equal(2, result.AnalyzerDiagnostics.Count);

            var cacheDiagnostics = result.AnalyzerDiagnostics.Where(d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId).ToList();
            var invalidateDiagnostics = result.AnalyzerDiagnostics.Where(d => d.Id == MethodCacheAnalyzer.InvalidateDiagnosticId).ToList();

            Assert.Single(cacheDiagnostics);
            Assert.Single(invalidateDiagnostics);

            Assert.Contains("GetValue", cacheDiagnostics[0].GetMessage());
            Assert.Contains("ClearCache", invalidateDiagnostics[0].GetMessage());
        }

        [Fact]
        public async Task GenericMethods_WithCacheAttribute_ShouldFollowSameRules()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class TestService
    {
        [Cache]
        public T GetValue<T>(int id) where T : class
        {
            return default(T);
        }

        [Cache]
        public virtual T GetVirtualValue<T>(int id) where T : class
        {
            return default(T);
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);

            // Should report diagnostic only for the non-virtual generic method
            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("GetValue", cacheDiagnostic.GetMessage());
        }

        [Fact]
        public async Task NestedClasses_ShouldBeAnalyzedCorrectly()
        {
            var source = @"
using MethodCache.Core;

namespace TestApp
{
    public class OuterService
    {
        public class NestedService
        {
            [Cache]
            public string GetValue(int id) => $""Value {id}"";

            [Cache]
            public virtual string GetVirtualValue(int id) => $""Virtual Value {id}"";
        }
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);

            // Should report diagnostic only for the non-virtual method in nested class
            var cacheDiagnostic = Assert.Single(result.AnalyzerDiagnostics, d => d.Id == MethodCacheAnalyzer.CacheDiagnosticId);
            Assert.Contains("GetValue", cacheDiagnostic.GetMessage());
        }

        [Fact]
        public async Task MethodsWithoutAttributes_ShouldNotReportDiagnostics()
        {
            var source = @"
namespace TestApp
{
    public class TestService
    {
        public string GetValue(int id) => $""Value {id}"";

        public static string GetStaticValue(int id) => $""Static Value {id}"";

        public virtual string GetVirtualValue(int id) => $""Virtual Value {id}"";
    }

    public abstract class AbstractService
    {
        public abstract string GetAbstractValue(int id);
    }
}";

            var result = await AnalyzeAsync(source);
            LogDiagnostics(result);

            Assert.Empty(result.CompilationDiagnostics);
            Assert.Empty(result.AnalyzerDiagnostics);
        }

        #endregion
    }
}
