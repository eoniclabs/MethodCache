using System.Reflection;
using MethodCache;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    public class MethodCacheGeneratorTests
    {
        private readonly ITestOutputHelper _output;

        public MethodCacheGeneratorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static Task<CSharpCompilation> CreateCompilation(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilities).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Threading.Tasks").Location)
            };

            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return Task.FromResult(compilation);
        }

        private async Task<GeneratedSourceResult> GetGeneratedSources(string source)
        {
            var compilation = await CreateCompilation(source);
            var generator = new MethodCacheGenerator();

            var driver = CSharpGeneratorDriver.Create(generator);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

            var result = new GeneratedSourceResult
            {
                Diagnostics = diagnostics.ToList(),
                GeneratedSources = new Dictionary<string, string>()
            };

            // Get all generated source files
            var generatedTrees = outputCompilation.SyntaxTrees.Skip(1); // Skip the original source
            foreach (var tree in generatedTrees)
            {
                var fileName = tree.FilePath ?? "Unknown";
                result.GeneratedSources[fileName] = tree.ToString();
            }

            return result;
        }

        private void LogGeneratedSources(GeneratedSourceResult result)
        {
            _output.WriteLine("=== GENERATED SOURCES ===");
            foreach (var kvp in result.GeneratedSources)
            {
                _output.WriteLine($"File: {kvp.Key}");
                _output.WriteLine(kvp.Value);
                _output.WriteLine("========================");
            }

            if (result.Diagnostics.Any())
            {
                _output.WriteLine("=== DIAGNOSTICS ===");
                foreach (var diagnostic in result.Diagnostics)
                {
                    _output.WriteLine($"{diagnostic.Severity}: {diagnostic.GetMessage()}");
                }
            }
        }

        public class GeneratedSourceResult
        {
            public Dictionary<string, string> GeneratedSources { get; set; } = new();
            public List<Diagnostic> Diagnostics { get; set; } = new();
        }

        private static string NormalizeWhitespace(string source)
        {
            if (string.IsNullOrEmpty(source))
                return source;

            // Normalize line endings and remove excessive whitespace while preserving structure
            return source
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\t", " ")
                .Trim();
        }

        private static void AssertContainsIgnoreWhitespace(string haystack, string needle)
        {
            // Normalize whitespace in both strings and then check contains
            var normalizedHaystack = System.Text.RegularExpressions.Regex.Replace(haystack, @"\s+", " ").Trim();
            var normalizedNeedle = System.Text.RegularExpressions.Regex.Replace(needle, @"\s+", " ").Trim();
            Assert.True(normalizedHaystack.Contains(normalizedNeedle),
                $"Expected to find pattern '{needle}' in generated source.\n" +
                $"Normalized needle: '{normalizedNeedle}'\n" +
                $"Normalized haystack: '{normalizedHaystack}'");
        }

        [Fact]
        public async Task GeneratesCacheMethodRegistryForCachedInterfaceMethod()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate the policy registrations file
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("class GeneratedPolicyRegistrations"));
            Assert.NotNull(registrySource);
            Assert.Contains("private static class GeneratedPolicyRegistrations", registrySource);
            Assert.Contains("public static void Describe", registrySource);
            Assert.Contains("GeneratedModuleBuilder", registrySource);
        }

        [Fact]
        public async Task GeneratesModuleInitializerThatRegistersDecorators()
        {
            var source = @"using System.Threading.Tasks;
using MethodCache;

namespace TestApp
{
    public interface IUserService
    {
        [Cache]
        Task<string> GetUserAsync(int id);
    }

    public interface IExternalService
    {
        [Cache]
        Task<string> GetAsync(int id);
    }

    public class AdminUserService : IUserService
    {
        public Task<string> GetUserAsync(int id) => Task.FromResult(""admin"");
    }

    public class UserService : IUserService
    {
        public Task<string> GetUserAsync(int id) => Task.FromResult(""user"");
    }

    internal class HiddenService : IUserService
    {
        private HiddenService() { }

        public Task<string> GetUserAsync(int id) => Task.FromResult(""hidden"");
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var registrations = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("ModuleInitializer"));
            Assert.NotNull(registrations);
            AssertContainsIgnoreWhitespace(registrations, "[global::System.Runtime.CompilerServices.ModuleInitializer]");
            AssertContainsIgnoreWhitespace(registrations, "builder.Decorator( typeof(global::TestApp.IUserService),");
            AssertContainsIgnoreWhitespace(registrations, "new global::TestApp.IUserServiceDecorator((global::TestApp.IUserService)inner,");

            // Every accessible implementation is described; runtime AutoRegister enforces uniqueness.
            AssertContainsIgnoreWhitespace(registrations, "builder.ImplementationCandidate(typeof(global::TestApp.IUserService), typeof(global::TestApp.UserService));");
            Assert.Contains("typeof(global::TestApp.AdminUserService)", registrations);
            Assert.DoesNotContain("HiddenService", registrations);

            // An interface without an implementation is still decorated when the application registers it
            AssertContainsIgnoreWhitespace(registrations, "builder.Decorator( typeof(global::TestApp.IExternalService),");
        }

        [Fact]
        public async Task GeneratesDecoratorForInterfaceInGlobalNamespace()
        {
            // Interfaces declared next to top-level statements live in the global namespace
            var source = @"using System.Threading.Tasks;
using MethodCache;

public interface IGlobalService
{
    [Cache]
    Task<string> GetAsync(int id);
}

public class GlobalService : IGlobalService
{
    public Task<string> GetAsync(int id) => Task.FromResult(""value"");
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.Single(s => s.Contains("internal sealed class IGlobalServiceDecorator"));
            Assert.Contains("namespace MethodCache.Generated", decoratorSource);
            Assert.DoesNotContain("<global namespace>", string.Concat(result.GeneratedSources.Values));

            var registrations = result.GeneratedSources.Values.Single(s => s.Contains("ModuleInitializer"));
            AssertContainsIgnoreWhitespace(registrations, "new global::MethodCache.Generated.IGlobalServiceDecorator(");
        }

        [Fact]
        public async Task GeneratorIncludesAdvancedFluentOptionsFromAttributes()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public class CustomKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
            => $""custom:{methodName}:{args.Length}"";
    }

    public interface ITestService
    {
        [Cache(Duration = ""00:10:00"", Tags = new[] { ""users"" }, Version = 7, SlidingExpiration = ""00:02:00"", RefreshAhead = ""00:00:30"")]
        string GetValue(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("class GeneratedPolicyRegistrations"));
            Assert.NotNull(registrySource);

            // New policy-based generation checks
            Assert.Contains("public static void Describe", registrySource);
            Assert.Contains("GeneratedMethodOptions", registrySource);
            Assert.Contains("GeneratedModuleBuilder", registrySource);
            Assert.Contains("builder.Method(", registrySource);
            AssertContainsIgnoreWhitespace(registrySource, $"SlidingExpiration = global::System.TimeSpan.FromTicks({TimeSpan.FromMinutes(2).Ticks}L)");
            AssertContainsIgnoreWhitespace(registrySource, $"RefreshAhead = global::System.TimeSpan.FromTicks({TimeSpan.FromSeconds(30).Ticks}L)");
            Assert.DoesNotContain("CachePolicyFields", registrySource);
            Assert.Contains("GeneratedMethodOptions", registrySource);
        }

        [Fact]
        public async Task GeneratesCacheDecoratorForCachedInterfaceMethod()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate the cache decorator
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "string global::TestApp.ITestService.GetValue(int __mcArgument0)");
            // Sync methods with simple parameters use the ultra-fast path with GetOrCreateFastAsync
            AssertContainsIgnoreWhitespace(decoratorSource, "__call.Load(__cacheKey,");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"GetValue\"");
            AssertContainsIgnoreWhitespace(decoratorSource, "static (__invocation, __state) => ((global::TestApp.ITestService)__invocation.ServiceForLoad(__state.Inner)).GetValue(__state.Arg0)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_method_Method0.Capture()");
            Assert.DoesNotContain("GetAwaiter", decoratorSource);
        }

        [Fact]
        public async Task GeneratesInvalidateDecoratorForInvalidateInterfaceMethod()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [CacheInvalidate(Tags = new[] { ""user"", ""data"" })]
        void ClearCache();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate the invalidate decorator
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.ClearCache()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_services.InvalidateTags");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"user\", \"data\"");
            Assert.DoesNotContain("GetAwaiter", decoratorSource);
        }

        [Fact]
        public async Task GeneratesCacheDecoratorForAsyncMethod()
        {
            var source = @"using MethodCache;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        Task<string> GetValueAsync(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate the cache decorator
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            // Ultra-fast path generates non-async method signature that returns Task
            AssertContainsIgnoreWhitespace(decoratorSource, "global::System.Threading.Tasks.Task<string> global::TestApp.ITestService.GetValueAsync(int __mcArgument0)");
            // With inline key optimization for simple parameter (int id), should use fast path with inline metrics
            // Task methods return the entry's completed task, so hits do not allocate
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcCall.TryGetTask<string>(ref __mcKeyBuilder, out var __mcCachedValue)");
            // The key is written to the stack, so a hit allocates no key string either
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcKeyBuilder.AppendPart(__mcArgument0);");
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcCall.TryGetTask<string>(ref __mcKeyBuilder, out var __mcCachedValue)");
            AssertContainsIgnoreWhitespace(decoratorSource, "return __mcCachedValue!;");
            Assert.DoesNotContain("_metricsProvider", decoratorSource);
            // The cache manager reports misses; the decorator reporting them too would count each miss twice.
            Assert.DoesNotContain("_metricsProvider?.CacheMiss", decoratorSource);
            AssertContainsIgnoreWhitespace(decoratorSource, "__call.LoadAsync(__cacheKey,");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"GetValueAsync\"");
            AssertContainsIgnoreWhitespace(decoratorSource, "static (__invocation, __state) => ((global::TestApp.ITestService)__invocation.ServiceForLoad(__state.Inner)).GetValueAsync(__state.Arg0)");
            Assert.DoesNotContain("async () =>", decoratorSource);
            AssertContainsIgnoreWhitespace(decoratorSource, "_method_Method0.Capture()");
            // Ultra-fast path returns Task.FromResult or createdTask directly, no async/await on the method
        }

        [Fact]
        public async Task GeneratesMultipleMethodsInSameInterface()
        {
            var source = @"using MethodCache;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);

        [Cache]
        Task<string> GetValueAsync(int id);

        [CacheInvalidate(Tags = new[] { ""user"" })]
        void ClearUserCache();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate policy registrations with both cached methods
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("class GeneratedPolicyRegistrations"));
            Assert.NotNull(registrySource);
            Assert.Contains("GeneratedMethodOptions", registrySource);
            Assert.Contains("builder.Method(", registrySource);

            // Should generate cache decorator with both methods
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "string global::TestApp.ITestService.GetValue(int __mcArgument0)");
            AssertContainsIgnoreWhitespace(decoratorSource, "global::System.Threading.Tasks.Task<string> global::TestApp.ITestService.GetValueAsync(int __mcArgument0)");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.ClearUserCache()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_method_Method0.Capture()");
            AssertContainsIgnoreWhitespace(decoratorSource, "InvalidateTags");
        }

        [Fact]
        public async Task GeneratesForMultipleInterfaces()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface IUserService
    {
        [Cache]
        string GetUser(int id);
    }

    public interface IProductService
    {
        [Cache]
        string GetProduct(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate policy registrations with both interfaces
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("class GeneratedPolicyRegistrations"));
            Assert.NotNull(registrySource);
            Assert.Contains("GeneratedMethodOptions", registrySource);
            Assert.Contains("builder.Method(", registrySource);

            // Should generate separate decorators
            var userDecoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("IUserServiceDecorator"));
            Assert.NotNull(userDecoratorSource);

            var productDecoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("IProductServiceDecorator"));
            Assert.NotNull(productDecoratorSource);
        }

        [Fact]
        public async Task GeneratesFluentConfigurationWithAttributeOptions()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface IUserService
    {
        [Cache(Duration = ""00:20:00"", Tags = new[] { ""users"", ""profile"" })]
        string GetProfile(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("class GeneratedPolicyRegistrations"));
            Assert.NotNull(registrySource);
            Assert.Contains("GeneratedMethodOptions", registrySource);
            Assert.Contains("builder.Method(", registrySource);
        }

        [Fact]
        public async Task HandlesMethodsWithComplexParameters()
        {
            var source = @"using MethodCache;
using System.Collections.Generic;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id, string name, List<int> items);

        [Cache]
        T GetGeneric<T>(int id) where T : class;
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "GetValue(int __mcArgument0, string __mcArgument1,");
            AssertContainsIgnoreWhitespace(decoratorSource, "List<int> __mcArgument2)");
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcTypeArgument0 global::TestApp.ITestService.GetGeneric");
            // Note: Generic constraints are not currently preserved by the source generator
            AssertContainsIgnoreWhitespace(decoratorSource, "((global::TestApp.ITestService)__invocation.ServiceForLoad(__state.Inner)).GetValue(__state.Arg0, __state.Arg1, __state.Arg2)");
            AssertContainsIgnoreWhitespace(decoratorSource, "((global::TestApp.ITestService)__invocation.ServiceForLoad(__state.Inner)).GetGeneric");
        }

        [Fact]
        public async Task HandlesMethodsWithNoParameters()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetConstantValue();

        [CacheInvalidate(Tags = new[] { ""all"" })]
        void ClearAll();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "string global::TestApp.ITestService.GetConstantValue()");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.ClearAll()");
            // Zero-parameter methods use the key prefix as the whole key
            AssertContainsIgnoreWhitespace(decoratorSource, "var __mcKey = __mcCall.KeyPrefix;");
            AssertContainsIgnoreWhitespace(decoratorSource, "((global::TestApp.ITestService)__invocation.ServiceForLoad(__state)).GetConstantValue()");
            AssertContainsIgnoreWhitespace(decoratorSource, "((global::TestApp.ITestService)this._decorated).ClearAll()");
        }

        [Fact]
        public async Task DoesNotGenerateForInterfaceWithoutAttributes()
        {
            var source = @"namespace TestApp
{
    public interface ITestService
    {
        string GetValue(int id);
        void DoSomething();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should not generate any files since no methods have cache attributes
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public async Task HandlesNestedNamespaces()
        {
            var source = @"using MethodCache;

namespace TestApp.Services.Data
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "namespace TestApp.Services.Data");
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.Services.Data.ITestService");
        }

        [Fact]
        public async Task HandlesVoidReturnType()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        void DoSomething(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // A void method can't be cached: error MC0003 stops the build, and the interface gets no decorator
            var error = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal("MC0003", error.Id);
            Assert.DoesNotContain(result.GeneratedSources.Values, s => s.Contains("class ITestServiceDecorator"));
        }

        [Fact]
        public async Task HandlesAsyncMethods()
        {
            var source = @"using MethodCache;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        Task<string> GetValueAsync(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            // Ultra-fast path generates non-async method signature that returns Task
            AssertContainsIgnoreWhitespace(decoratorSource, "global::System.Threading.Tasks.Task<string> global::TestApp.ITestService.GetValueAsync(int __mcArgument0)");
            // With inline key optimization for simple parameter (int id), should use fast path with inline metrics
            // Task methods return the entry's completed task, so hits do not allocate
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcCall.TryGetTask<string>(ref __mcKeyBuilder, out var __mcCachedValue)");
            // The key is written to the stack, so a hit allocates no key string either
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcKeyBuilder.AppendPart(__mcArgument0);");
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcCall.TryGetTask<string>(ref __mcKeyBuilder, out var __mcCachedValue)");
            AssertContainsIgnoreWhitespace(decoratorSource, "return __mcCachedValue!;");
            Assert.DoesNotContain("_metricsProvider", decoratorSource);
            // The cache manager reports misses; the decorator reporting them too would count each miss twice.
            Assert.DoesNotContain("_metricsProvider?.CacheMiss", decoratorSource);
            AssertContainsIgnoreWhitespace(decoratorSource, "__call.LoadAsync(__cacheKey,");
            AssertContainsIgnoreWhitespace(decoratorSource, "static (__invocation, __state) => ((global::TestApp.ITestService)__invocation.ServiceForLoad(__state.Inner)).GetValueAsync(__state.Arg0)");
            Assert.DoesNotContain("async () =>", decoratorSource);
        }

        [Fact]
        public async Task HandlesMultipleInvalidateTagsCorrectly()
        {
            var source = @"using MethodCache;

namespace TestApp
{
    public interface ITestService
    {
        [CacheInvalidate(Tags = new[] { ""users"", ""data"", ""cache"" })]
        void ClearMultipleTags();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "internal sealed class ITestServiceDecorator : global::TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.ClearMultipleTags()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_services.InvalidateTags");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"users\", \"data\", \"cache\"");
        }

        [Fact]
        public async Task HandlesEmptyInterface()
        {
            var source = @"namespace TestApp
{
    public interface IEmptyService
    {
        void RegularMethod();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should not generate any files since no methods have cache attributes
            Assert.Empty(result.GeneratedSources);
        }

        [Fact]
        public async Task HandlesMixedMethodTypes()
        {
            var source = @"using MethodCache;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);
        
        [Cache]
        Task<string> GetValueAsync(int id);
        
        [CacheInvalidate(Tags = new[] { ""all"" })]
        void ClearAll();
        
        void RegularMethod();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("internal sealed class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);

            // Should generate all methods including non-cached ones
            AssertContainsIgnoreWhitespace(decoratorSource, "string global::TestApp.ITestService.GetValue(int __mcArgument0)");
            // Ultra-fast path generates non-async method signature that returns Task
            AssertContainsIgnoreWhitespace(decoratorSource, "global::System.Threading.Tasks.Task<string> global::TestApp.ITestService.GetValueAsync(int __mcArgument0)");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.ClearAll()");
            AssertContainsIgnoreWhitespace(decoratorSource, "void global::TestApp.ITestService.RegularMethod()");

            // Check caching logic for cached methods (should use fast path for simple int parameter with inline metrics)
            AssertContainsIgnoreWhitespace(decoratorSource, "__mcCall.TryGetValue<string>(ref __mcKeyBuilder, out var __mcCachedValue)");
            Assert.DoesNotContain("_metricsProvider", decoratorSource);
            // The cache manager reports misses; the decorator reporting them too would count each miss twice.
            Assert.DoesNotContain("_metricsProvider?.CacheMiss", decoratorSource);
            AssertContainsIgnoreWhitespace(decoratorSource, "__call.LoadAsync(__cacheKey,");
            AssertContainsIgnoreWhitespace(decoratorSource, "_services.InvalidateTags");

            // Check pass-through for regular method
            AssertContainsIgnoreWhitespace(decoratorSource, "((global::TestApp.ITestService)this._decorated).RegularMethod()");
        }

        [Fact]
        public void NullableSignatures_CompileWithoutWarningsInGeneratedCode()
        {
            var source = @"
#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

namespace TestApp
{
    public sealed record User(string Name);

    public interface ITestService
    {
        [Cache]
        Task<User?> FindAsync(string? name);

        [Cache]
        ValueTask<IReadOnlyList<User?>> ListAsync(int[]? ids);

        [Cache]
        string? Describe(int? id);
    }
}";
            // Everything the test run can load: the framework plus MethodCache and its dependencies
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                .GroupBy(Path.GetFileName)
                .Select(g => MetadataReference.CreateFromFile(g.First()))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            CSharpGeneratorDriver.Create(new MethodCacheGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            var generatedTrees = outputCompilation.SyntaxTrees.Skip(1).ToHashSet();
            var problems = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning && generatedTrees.Contains(d.Location.SourceTree!))
                .ToList();
            foreach (var problem in problems)
            {
                _output.WriteLine(problem.ToString());
                _output.WriteLine(problem.Location.SourceTree!.GetText().Lines[problem.Location.GetLineSpan().StartLinePosition.Line].ToString());
            }

            Assert.Empty(problems);
        }
    }
}
