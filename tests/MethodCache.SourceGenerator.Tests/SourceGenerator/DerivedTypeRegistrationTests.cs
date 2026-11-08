using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>
    /// The generator registers the types that derive from a type a cached result declares, wherever they are: in the
    /// project, in the declared type's assembly or one built on it, or in a project that references the one with the
    /// cached interface. A derived type the generated code can't name gets MC0039 instead.
    /// </summary>
    public class DerivedTypeRegistrationTests
    {
        private const string Zoo = @"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

namespace Contracts
{
    public class Animal { public string Name { get; set; } = """"; }
    public interface IZoo
    {
        [Cache] Task<List<Animal>> AnimalsAsync();
    }
}";

        [Fact]
        public void ADerivedTypeInTheProject_IsRegistered_AndItsBaseIsListedForReferencingProjects()
        {
            var run = Run(Compilation("Contracts", Zoo, "namespace Contracts { public class Dog : Animal { public string Breed { get; set; } = \"\"; } }"));

            Assert.Contains("builder.DerivedTypes(typeof(global::Contracts.Animal), typeof(global::Contracts.Dog));", run.Source("MethodCacheDecoratorRegistrations.g.cs"));
            Assert.Contains("AssemblyMetadata(\"MethodCache.DerivedTypeBase\", \"global::Contracts.Animal\")", run.Source("MethodCacheDerivedTypeBases.g.cs"));
        }

        [Fact]
        public void ADerivedTypeInAProjectThatReferencesTheCachedInterface_IsRegisteredThere()
        {
            var contracts = Image(Run(Compilation("Contracts", Zoo)).Output);

            var app = Run(Compilation("App", "namespace App { public class Dog : Contracts.Animal { public string Breed { get; set; } = \"\"; } }", contracts));

            var registrations = app.Source("MethodCacheDecoratorRegistrations.g.cs");
            Assert.Contains("builder.DerivedTypes(typeof(global::Contracts.Animal), typeof(global::App.Dog));", registrations);
            Assert.DoesNotContain("DeclareService", registrations);
        }

        [Fact]
        public void ADerivedTypeInAnAssemblyBuiltOnTheDeclaredTypes_IsRegistered()
        {
            var domain = Image(Compilation("Domain", "namespace Domain { public class Animal { public string Name { get; set; } = \"\"; } }"));
            var extensions = Image(Compilation("Extensions", "namespace Extensions { public class Dog : Domain.Animal { public string Breed { get; set; } = \"\"; } }", domain));

            var run = Run(Compilation("Contracts", @"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;
namespace Contracts { public interface IZoo { [Cache] Task<List<Domain.Animal>> AnimalsAsync(); } }", domain, extensions));

            Assert.Contains("builder.DerivedTypes(typeof(global::Domain.Animal), typeof(global::Extensions.Dog));", run.Source("MethodCacheDecoratorRegistrations.g.cs"));
        }

        [Fact]
        public void ADerivedTypeTheGeneratedCodeCantName_GetsMC0039_AndIsNotRegistered()
        {
            var run = Run(Compilation("Contracts", Zoo, @"
namespace Contracts
{
    public class Box<T> : Animal { public T Value { get; set; } = default!; }
    public class Keeper { private sealed class Secret : Animal { } }
    file sealed class Local : Animal { }
    public abstract class Mammal : Animal { }
}"));

            var warnings = run.Diagnostics.Where(d => d.Id == "MC0039").Select(d => d.GetMessage()).OrderBy(m => m, StringComparer.Ordinal).ToList();
            Assert.Collection(warnings,
                m => Assert.StartsWith("'Contracts.Box<T>' derives from 'Contracts.Animal', which a cached result holds, but it is generic,", m),
                m => Assert.StartsWith("'Contracts.Keeper.Secret' derives from 'Contracts.Animal', which a cached result holds, but it is private,", m),
                m => Assert.Contains("but it is file-local,", m));
            Assert.DoesNotContain("builder.DerivedTypes", run.Source("MethodCacheDecoratorRegistrations.g.cs"));
        }

        [Fact]
        public void ADerivedTypeThatIsNotPublicInAReferencedAssembly_GetsMC0039_AtTheCachedMethod()
        {
            var domain = Image(Compilation("Domain", @"
namespace Domain
{
    public class Animal { public string Name { get; set; } = """"; }
    public class Dog : Animal { }
    internal class Hidden : Animal { }
}"));

            var run = Run(Compilation("Contracts", @"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;
namespace Contracts { public interface IZoo { [Cache] Task<List<Domain.Animal>> AnimalsAsync(); } }", domain));

            var warning = Assert.Single(run.Diagnostics, d => d.Id == "MC0039");
            Assert.StartsWith("'Domain.Hidden' derives from 'Domain.Animal', which a cached result holds, but it is not public,", warning.GetMessage());
            Assert.Contains("AnimalsAsync", warning.Location.SourceTree!.ToString().Substring(warning.Location.SourceSpan.Start));
            Assert.Contains("builder.DerivedTypes(typeof(global::Domain.Animal), typeof(global::Domain.Dog));", run.Source("MethodCacheDecoratorRegistrations.g.cs"));
        }

        [Fact]
        public void ATestProject_GetsNoMC0039_ForItsPrivateTestDoubles()
        {
            var run = Run(Compilation("Contracts.Tests", Zoo, "namespace Contracts { public class Tests { private sealed class FakeAnimal : Animal { } } }")
                .AddReferences(MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location)));

            Assert.DoesNotContain(run.Diagnostics, d => d.Id == "MC0039");
        }

        private sealed record RunResult(Compilation Output, IReadOnlyList<Diagnostic> Diagnostics)
        {
            public string Source(string fileName) => Output.SyntaxTrees.Single(t => t.FilePath.EndsWith(fileName, StringComparison.Ordinal)).ToString();
        }

        private static RunResult Run(CSharpCompilation compilation)
        {
            CSharpGeneratorDriver.Create(new MethodCacheGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
            return new RunResult(output, diagnostics);
        }

        private static CSharpCompilation Compilation(string name, string source, params MetadataReference[] references)
            => Compilation(name, new[] { source }, references);

        private static CSharpCompilation Compilation(string name, string source, string more, params MetadataReference[] references)
            => Compilation(name, new[] { source, more }, references);

        private static CSharpCompilation Compilation(string name, string[] sources, MetadataReference[] references)
            => CSharpCompilation.Create(
                name,
                sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest))),
                References().Concat(references),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        private static MetadataReference Image(Compilation compilation)
        {
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return MetadataReference.CreateFromImage(image.ToArray());
        }

        // The framework plus MethodCache and its dependencies, without the test framework, which makes a test project
        private static IEnumerable<MetadataReference> References()
            => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                .Where(path => !Path.GetFileName(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
                .GroupBy(Path.GetFileName)
                .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()));
    }
}
