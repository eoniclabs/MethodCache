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
        [Cache(Duration = ""00:05:00"")]
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
        [Cache(Duration = ""00:01:00"")]
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

        private static readonly string[] TrackedSteps = { "ExternalInterfaces", "Interfaces", "Classes", "Decorators", "Summaries" };

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

        private static void AssertAllReused(GeneratorRunResult result, string stepName)
        {
            Assert.True(result.TrackedSteps.TryGetValue(stepName, out var runs), $"Step {stepName} was not tracked");
            Assert.All(runs.SelectMany(r => r.Outputs),
                output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"Step {stepName} ran again: {output.Reason}"));
        }

        private static (GeneratorDriver Driver, CSharpCompilation Compilation) RunInitial()
        {
            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { UserService, OrderService, Implementation }.Select(s => CSharpSyntaxTree.ParseText(s)),
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
