using Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MethodCache.SourceGenerator;
using System.Collections.Generic;
using System;
using Xunit.Abstractions;

namespace MethodCache.Tests.SourceGenerator
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
                MetadataReference.CreateFromFile(typeof(MethodCache.Core.CacheAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(MethodCache.Core.Configuration.MethodCacheConfiguration).Assembly.Location),
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
            var source = @"using MethodCache.Core;

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

            // Should generate the registry file
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("GeneratedCacheMethodRegistry"));
            Assert.NotNull(registrySource);
            Assert.Contains("internal class GeneratedCacheMethodRegistry : ICacheMethodRegistry", registrySource);
            Assert.Contains("config.ApplyFluent(fluent =>", registrySource);
            Assert.Contains("fluent.ForService<TestApp.ITestService>()", registrySource);
            Assert.Contains(".Method(x => x.GetValue(Any<int>.Value));", registrySource);
        }

        [Fact]
        public async Task GeneratorIncludesAdvancedFluentOptionsFromAttributes()
        {
            var source = @"using MethodCache.Core;
using MethodCache.Core.Configuration;

namespace TestApp
{
    public class CustomKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
            => $""custom:{methodName}:{args.Length}"";
    }

    public interface ITestService
    {
        [Cache(Duration = ""00:10:00"", Tags = new[] { ""users"" }, Version = 7, KeyGeneratorType = typeof(CustomKeyGenerator))]
        string GetValue(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("GeneratedCacheMethodRegistry"));
            Assert.NotNull(registrySource);

            AssertContainsIgnoreWhitespace(registrySource!, ".Configure(options =>");
            AssertContainsIgnoreWhitespace(registrySource!, "options.WithDuration(System.TimeSpan.Parse(\"00:10:00\"));");
            AssertContainsIgnoreWhitespace(registrySource!, "options.WithTags(\"users\");");
            AssertContainsIgnoreWhitespace(registrySource!, "options.WithVersion(7);");
            AssertContainsIgnoreWhitespace(registrySource!, "options.WithKeyGenerator<TestApp.CustomKeyGenerator>();");
        }

        [Fact]
        public async Task GeneratesCacheDecoratorForCachedInterfaceMethod()
        {
            var source = @"using MethodCache.Core;

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
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public string GetValue(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.GetOrCreateAsync<string>");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"GetValue\"");
            AssertContainsIgnoreWhitespace(decoratorSource, "() => Task.FromResult(_decorated.GetValue(id))");
            AssertContainsIgnoreWhitespace(decoratorSource, "_keyGenerator, settings.IsIdempotent");
            AssertContainsIgnoreWhitespace(decoratorSource, ".GetAwaiter().GetResult()");
        }

        [Fact]
        public async Task GeneratesInvalidateDecoratorForInvalidateInterfaceMethod()
        {
            var source = @"using MethodCache.Core;

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
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void ClearCache()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.InvalidateByTagsAsync");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"user\", \"data\"");
            AssertContainsIgnoreWhitespace(decoratorSource, ".GetAwaiter().GetResult()");
        }

        [Fact]
        public async Task GeneratesCacheDecoratorWithRequireIdempotentTrue()
        {
            var source = @"using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache(RequireIdempotent = true)]
        Task<string> GetValueAsync(int id);
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate the cache decorator with idempotent flag
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "Task<string> GetValueAsync(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.GetOrCreateAsync");
            AssertContainsIgnoreWhitespace(decoratorSource, "\"GetValueAsync\"");
            AssertContainsIgnoreWhitespace(decoratorSource, "async () => await _decorated.GetValueAsync(id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_keyGenerator, settings.IsIdempotent");
            // Async methods return Task directly, no GetAwaiter().GetResult()
        }

        [Fact]
        public async Task GeneratesMultipleMethodsInSameInterface()
        {
            var source = @"using MethodCache.Core;
using System.Threading.Tasks;

namespace TestApp
{
    public interface ITestService
    {
        [Cache]
        string GetValue(int id);

        [Cache(RequireIdempotent = true)]
        Task<string> GetValueAsync(int id);

        [CacheInvalidate(Tags = new[] { ""user"" })]
        void ClearUserCache();
    }
}";

            var result = await GetGeneratedSources(source);
            LogGeneratedSources(result);

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            // Should generate registry with both cached methods
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("GeneratedCacheMethodRegistry"));
            Assert.NotNull(registrySource);
            Assert.Contains("config.ApplyFluent(fluent =>", registrySource);
            Assert.Contains("fluent.ForService<TestApp.ITestService>()", registrySource);
            Assert.Contains(".Method(x => x.GetValue(Any<int>.Value));", registrySource);
            AssertContainsIgnoreWhitespace(registrySource, ".Method(x => x.GetValueAsync(Any<int>.Value))");
            Assert.Contains(".RequireIdempotent(true);", registrySource);

            // Should generate cache decorator with both methods
            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public string GetValue(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "Task<string> GetValueAsync(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void ClearUserCache()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_keyGenerator, settings.IsIdempotent");
            AssertContainsIgnoreWhitespace(decoratorSource, "InvalidateByTagsAsync");
        }

        [Fact]
        public async Task GeneratesForMultipleInterfaces()
        {
            var source = @"using MethodCache.Core;

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

            // Should generate registry with both interfaces
            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("GeneratedCacheMethodRegistry"));
            Assert.NotNull(registrySource);
            Assert.Contains("config.ApplyFluent(fluent =>", registrySource);
            Assert.Contains("fluent.ForService<TestApp.IUserService>()", registrySource);
            Assert.Contains(".Method(x => x.GetUser(Any<int>.Value));", registrySource);
            Assert.Contains("fluent.ForService<TestApp.IProductService>()", registrySource);
            Assert.Contains(".Method(x => x.GetProduct(Any<int>.Value));", registrySource);

            // Should generate separate decorators
            var userDecoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("IUserServiceDecorator"));
            Assert.NotNull(userDecoratorSource);

            var productDecoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("IProductServiceDecorator"));
            Assert.NotNull(productDecoratorSource);
        }

        [Fact]
        public async Task GeneratesFluentConfigurationWithAttributeOptions()
        {
            var source = @"using MethodCache.Core;

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

            var registrySource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("GeneratedCacheMethodRegistry"));
            Assert.NotNull(registrySource);
            Assert.Contains("fluent.ForService<TestApp.IUserService>()", registrySource);
            Assert.Contains(".Method(x => x.GetProfile(Any<int>.Value))", registrySource);
            Assert.Contains("options.WithDuration(System.TimeSpan.Parse(\"00:20:00\"));", registrySource);
            Assert.Contains("options.WithTags(\"users\", \"profile\");", registrySource);
        }

        [Fact]
        public async Task HandlesMethodsWithComplexParameters()
        {
            var source = @"using MethodCache.Core;
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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "GetValue(int id, string name,");
            AssertContainsIgnoreWhitespace(decoratorSource, "List<int> items)");
            AssertContainsIgnoreWhitespace(decoratorSource, "public T GetGeneric");
            // Note: Generic constraints are not currently preserved by the source generator
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.GetValue(id, name, items)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.GetGeneric");
        }

        [Fact]
        public async Task HandlesMethodsWithNoParameters()
        {
            var source = @"using MethodCache.Core;

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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public string GetConstantValue()");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void ClearAll()");
            AssertContainsIgnoreWhitespace(decoratorSource, "var args = new object[] { };"); // No parameters
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.GetConstantValue()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.ClearAll()");
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
            var source = @"using MethodCache.Core;

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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "namespace TestApp.Services.Data");
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.Services.Data.ITestService");
        }

        [Fact]
        public async Task HandlesVoidReturnType()
        {
            var source = @"using MethodCache.Core;

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

            // Should have no compilation errors
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void DoSomething(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.DoSomething(id)");
        }

        [Fact]
        public async Task HandlesAsyncMethods()
        {
            var source = @"using MethodCache.Core;
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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public System.Threading.Tasks.Task<string> GetValueAsync(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.GetOrCreateAsync<string>");
            AssertContainsIgnoreWhitespace(decoratorSource, "async () => await _decorated.GetValueAsync(id)");
        }

        [Fact]
        public async Task HandlesMultipleInvalidateTagsCorrectly()
        {
            var source = @"using MethodCache.Core;

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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Check for expected patterns with flexible whitespace matching
            AssertContainsIgnoreWhitespace(decoratorSource, "public class ITestServiceDecorator : TestApp.ITestService");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void ClearMultipleTags()");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.InvalidateByTagsAsync");
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
            var source = @"using MethodCache.Core;
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

            var decoratorSource = result.GeneratedSources.Values.FirstOrDefault(s => s.Contains("public class ITestServiceDecorator"));
            Assert.NotNull(decoratorSource);
            
            // Should generate all methods including non-cached ones
            AssertContainsIgnoreWhitespace(decoratorSource, "public string GetValue(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "public System.Threading.Tasks.Task<string> GetValueAsync(int id)");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void ClearAll()");
            AssertContainsIgnoreWhitespace(decoratorSource, "public void RegularMethod()");
            
            // Check caching logic for cached methods
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.GetOrCreateAsync<string>");
            AssertContainsIgnoreWhitespace(decoratorSource, "_cacheManager.InvalidateByTagsAsync");
            
            // Check pass-through for regular method
            AssertContainsIgnoreWhitespace(decoratorSource, "_decorated.RegularMethod()");
        }
    }
}
