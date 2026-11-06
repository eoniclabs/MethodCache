using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// A project whose build didn't run MethodCache's source generator: its [Cache] services would run uncached, so
/// AddMethodCache fails and names the project.
/// </summary>
public class MissingGeneratorTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using MethodCache;

        namespace Data;

        public interface IBase { [Cache] Task<int> GetAsync(int id); }
        public interface IRepo : IBase { }
        public interface IPlain { Task<int> GetAsync(int id); }

        public sealed class Repo : IRepo, IPlain
        {
            public Task<int> GetAsync(int id) => Task.FromResult(id);
        }
        """;

    [Theory]
    [InlineData("Data.IBase")]
    [InlineData("Data.IRepo")]
    public void ServiceWithCacheMethodsAndNoGeneratedCode_FailsAddMethodCache(string serviceName)
    {
        var assembly = CompileWithoutGenerator("Data_" + Guid.NewGuid().ToString("N"));
        var service = assembly.GetType(serviceName)!;
        var services = new ServiceCollection();
        services.AddSingleton(service, assembly.GetType("Data.Repo")!);

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache());

        Assert.Contains($"'{service}' has [Cache] methods, but MethodCache's source generator didn't run in {assembly.GetName().Name}", error.Message, StringComparison.Ordinal);
        Assert.Contains("Dependencies > Analyzers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceWithoutCacheMethods_IsLeftAlone()
    {
        var assembly = CompileWithoutGenerator("Data_" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddSingleton(assembly.GetType("Data.IPlain")!, assembly.GetType("Data.Repo")!);

        services.AddMethodCache();
    }

    private static Assembly CompileWithoutGenerator(string name)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = tpa.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
        if (!tpa.Contains(typeof(CacheAttribute).Assembly.Location))
            references.Add(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location));

        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
}
