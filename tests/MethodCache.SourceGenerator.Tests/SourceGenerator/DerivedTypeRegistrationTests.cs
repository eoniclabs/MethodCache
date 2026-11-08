using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static MethodCache.SourceGenerator.Tests.SourceGenerator.GeneratorRun;

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

        private const string Setup = @"
using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace App
{
    // What System.Text.Json's source generator adds, which this test doesn't run
    internal partial class AppJsonContext { public static AppJsonContext Default => null!; }

    LISTED
    internal partial class AppJsonContext : JsonSerializerContext { }

    public static class Setup
    {
        public static void Configure(MethodCache.MethodCacheBuilder mc) => mc.UseJson(AppJsonContext.Default);
    }
}";

        [Theory]
        [InlineData("[JsonSerializable(typeof(List<Contracts.Animal>))]", true)]
        [InlineData("[JsonSerializable(typeof(List<Contracts.Animal>))] [JsonSerializable(typeof(Contracts.Dog))]", false)]
        [InlineData("[JsonSerializable(typeof(Contracts.Animal))] [JsonSerializable(typeof(List<Contracts.Dog>))]", false)]
        [InlineData("[JsonSerializable(typeof(string))]", false)]
        public void ADerivedTypeTheContextPassedToUseJsonLacks_GetsMC0040(string listed, bool reported)
        {
            var run = Run(Compilation("Contracts", new[] { Zoo, "namespace Contracts { public class Dog : Animal { } }", Setup.Replace("LISTED", listed) }, []), allowErrors: true);

            var warnings = run.Diagnostics.Where(d => d.Id == "MC0040").ToList();
            if (!reported)
            {
                Assert.Empty(warnings);
                return;
            }

            var warning = Assert.Single(warnings);
            Assert.StartsWith("'App.AppJsonContext' has no contract for 'Contracts.Dog', which derives from 'Contracts.Animal'", warning.GetMessage());
            Assert.Equal("AppJsonContext.Default", warning.Location.SourceTree!.ToString().Substring(warning.Location.SourceSpan.Start, warning.Location.SourceSpan.Length));
            Assert.Equal("T:App.AppJsonContext", warning.Properties["Context"]);
            Assert.Equal("global::Contracts.Dog", warning.Properties["DerivedType"]);
        }

        [Fact]
        public void ADerivedTypeAReferencedProjectRegisters_IsCheckedAgainstTheContext()
        {
            var contracts = Image(Run(Compilation("Contracts", Zoo, "namespace Contracts { public class Dog : Animal { } }")).Output);

            var run = Run(Compilation("App", Setup.Replace("LISTED", "[JsonSerializable(typeof(List<Contracts.Animal>))]"), contracts), allowErrors: true);

            Assert.Contains(run.Diagnostics, d => d.Id == "MC0040" && d.GetMessage().Contains("'Contracts.Dog'", StringComparison.Ordinal));
        }
    }
}
