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

        private static void AssertAllReused(GeneratorRunResult result, string stepName)
        {
            Assert.True(result.TrackedSteps.TryGetValue(stepName, out var runs), $"Step {stepName} was not tracked");
            Assert.All(runs.SelectMany(r => r.Outputs),
                output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"Step {stepName} ran again: {output.Reason}"));
        }

        private static (GeneratorDriver Driver, CSharpCompilation Compilation) RunInitial()
        {
            // Everything the test run can load: the framework plus MethodCache and its dependencies
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                .GroupBy(Path.GetFileName)
                .Select(g => MetadataReference.CreateFromFile(g.First()))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                "compilation",
                new[] { UserService, OrderService, Implementation }.Select(s => CSharpSyntaxTree.ParseText(s)),
                references,
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

        private static CSharpCompilation Replace(CSharpCompilation compilation, string oldSource, string newSource)
        {
            var oldTree = compilation.SyntaxTrees.Single(t => t.ToString() == oldSource);
            return compilation.ReplaceSyntaxTree(oldTree, CSharpSyntaxTree.ParseText(newSource));
        }
    }
}
