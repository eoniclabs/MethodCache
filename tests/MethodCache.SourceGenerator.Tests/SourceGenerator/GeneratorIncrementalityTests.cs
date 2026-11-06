using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>
    /// The IDE runs the generator on every edit. These tests check that an edit only redoes the work it affects.
    /// </summary>
    public class GeneratorIncrementalityTests
    {
        private const string UserService = @"
using System.Threading.Tasks;
using MethodCache;

namespace App
{
    public interface IUserService
    {
        [Cache(Duration = ""5m"")]
        Task<string> GetUserAsync(int id);
    }
}";

        private const string OrderService = @"
using System.Threading.Tasks;
using MethodCache;

namespace App
{
    public interface IOrderService
    {
        [Cache(Duration = ""1m"")]
        Task<string> GetOrderAsync(int id);
    }
}";

        private const string Implementation = @"
using System.Threading.Tasks;

namespace App
{
    public class UserService : IUserService
    {
        public Task<string> GetUserAsync(int id) => Task.FromResult(""user"" + id);
    }
}";

        private static readonly string[] TrackedSteps = { "ExternalInterfaces", "Interfaces", "Classes", "Decorators", "Summaries", "EntityMapping", "HostMarkers" };

        [Fact]
        public void EditingAMethodBody_RegeneratesNothing()
        {
            var (driver, compilation) = RunInitial();

            var edited = Replace(compilation, Implementation, Implementation.Replace("\"user\" + id", "\"member\" + id"));
            var result = driver.RunGenerators(edited).GetRunResult().Results.Single();

            foreach (var step in TrackedSteps)
            {
                AssertAllReused(result, step);
            }

            Assert.All(result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs),
                output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"An output ran again: {output.Reason}"));
        }

        [Fact]
        public void EditingOneInterface_RegeneratesOnlyItsDecorator()
        {
            var (driver, compilation) = RunInitial();

            var edited = Replace(compilation, OrderService, OrderService.Replace("GetOrderAsync(int id)", "GetOrderAsync(long id)"));
            var result = driver.RunGenerators(edited).GetRunResult().Results.Single();

            var decorators = result.TrackedSteps["Decorators"].SelectMany(s => s.Outputs).ToList();
            var modified = Assert.Single(decorators, o => o.Reason == IncrementalStepRunReason.Modified);
            Assert.Contains("IOrderServiceDecorator", (((string HintName, string Source))modified.Value).HintName);
            Assert.Contains(decorators, o => o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);

            // The class list did not change, so the decorator registrations are not regenerated
            AssertAllReused(result, "Classes");
        }

        [Fact]
        public void EveryEdit_BuildsADecoratorOncePerInterface_NotOncePerAttributedMethod()
        {
            // The attribute transform runs again whenever the compilation changes; it must not build the whole
            // decorator again for each of the interface's attributed methods
            const string service = @"
using System.Threading.Tasks;
using MethodCache;

namespace App
{
    public interface ISvc
    {
        [Cache] Task<int> A(int id);
        [Cache] Task<int> B(int id);
        [Cache] Task<int> C(int id);
        [Cache] Task<int> D(int id);
        [CacheInvalidate(Tags = new[] { ""all"" })] Task E(int id);
    }
}";
            const string other = "namespace App { public static class Other { public static int X() => 1; } }";
            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { service, other }.Select(s => CSharpSyntaxTree.ParseText(s)),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new MethodCacheGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

            driver = driver.RunGenerators(compilation);
            Assert.Equal(1, DecoratorBuilds(driver.GetRunResult().Results.Single()));

            var edited = Replace(compilation, other, other.Replace("=> 1", "=> 2"));
            Assert.Equal(1, DecoratorBuilds(driver.RunGenerators(edited).GetRunResult().Results.Single()));

            static int DecoratorBuilds(GeneratorRunResult result) => result.TrackedSteps
                .Where(s => s.Key.StartsWith("result_ForAttributeWithMetadataName", StringComparison.Ordinal))
                .SelectMany(s => s.Value.SelectMany(r => r.Outputs))
                .Count(o => o.Value?.ToString()?.Contains("ISvc", StringComparison.Ordinal) == true);
        }

        // MethodCache.EntityFrameworkCore's mapping API, so the test needs no EF Core package
        private const string EntityFrameworkCoreOptions = @"
namespace MethodCache.EntityFrameworkCore
{
    public sealed class EntityFrameworkCoreOptions
    {
        public EntityFrameworkCoreOptions Entity<TEntity>(params string[] tagTemplates) where TEntity : class => this;
    }
}";

        private const string Mapping = @"
namespace App
{
    public record Product(int Id);
    public record Order(int Id);
    public static class Startup
    {
        public static void Map(MethodCache.EntityFrameworkCore.EntityFrameworkCoreOptions ef) => ef.Entity<Product>(""products"");
    }
}";

        [Fact]
        public void The_entity_mapping_is_written_again_only_when_a_mapping_call_changes()
        {
            var (driver, compilation) = RunInitial(EntityFrameworkCoreOptions, Mapping);
            Assert.Contains("entity:any:App.Product", MappingSource(driver));

            var bodyEdited = Replace(compilation, Implementation, Implementation.Replace("\"user\" + id", "\"member\" + id"));
            driver = driver.RunGenerators(bodyEdited);
            AssertAllReused(driver.GetRunResult().Results.Single(), "EntityMapping");

            var mappingEdited = Replace(bodyEdited, Mapping, Mapping.Replace("ef.Entity<Product>(\"products\")", "ef.Entity<Product>(\"products\").Entity<Order>(\"orders\")"));
            driver = driver.RunGenerators(mappingEdited);
            var mapping = driver.GetRunResult().Results.Single().TrackedSteps["EntityMapping"].SelectMany(s => s.Outputs);
            Assert.Contains(mapping, o => o.Reason == IncrementalStepRunReason.Modified);
            Assert.Contains("entity:any:App.Order", MappingSource(driver));

            static string MappingSource(GeneratorDriver driver)
                => driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.EndsWith("MethodCacheEntityMapping.g.cs", StringComparison.Ordinal)).ToString();
        }

        private static void AssertAllReused(GeneratorRunResult result, string stepName)
        {
            Assert.True(result.TrackedSteps.TryGetValue(stepName, out var runs), $"Step {stepName} was not tracked");
            Assert.All(runs.SelectMany(r => r.Outputs),
                output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"Step {stepName} ran again: {output.Reason}"));
        }

        private static (GeneratorDriver Driver, CSharpCompilation Compilation) RunInitial(params string[] moreSources)
        {
            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { UserService, OrderService, Implementation }.Concat(moreSources).Select(s => CSharpSyntaxTree.ParseText(s)),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new MethodCacheGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
            driver = driver.RunGenerators(compilation);

            var first = driver.GetRunResult();
            Assert.Empty(first.Diagnostics);
            Assert.Contains(first.GeneratedTrees, t => t.FilePath.EndsWith("IUserServiceDecorator.g.cs", StringComparison.Ordinal));
            return (driver, compilation);
        }

        // Everything the test run can load: the framework plus MethodCache and its dependencies
        private static MetadataReference[] References()
            => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                .GroupBy(Path.GetFileName)
                .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()))
                .ToArray();

        private static CSharpCompilation Replace(CSharpCompilation compilation, string oldSource, string newSource)
        {
            var oldTree = compilation.SyntaxTrees.Single(t => t.ToString() == oldSource);
            return compilation.ReplaceSyntaxTree(oldTree, CSharpSyntaxTree.ParseText(newSource));
        }
    }
}
