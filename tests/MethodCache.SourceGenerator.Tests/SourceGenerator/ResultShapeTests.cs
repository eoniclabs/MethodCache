using System.Text.RegularExpressions;
using Xunit;
using static MethodCache.SourceGenerator.Tests.SourceGenerator.GeneratorRun;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>
    /// The fingerprint of a cached result's shape, which the distributed cache keeps a method's entries apart by: it
    /// changes with what a serializer writes, and stays the same for every other edit.
    /// </summary>
    public class ResultShapeTests
    {
        private const string Before = @"
public enum Status { Open = 1, Closed = 2 }
public sealed class Line { public string Sku { get; set; } = """"; public int Quantity { get; set; } }
public sealed class Order
{
    private int _reads;
    public int Id { get; set; }
    public Status Status { get; set; }
    public List<Line> Lines { get; set; } = new();
    public int Reads() => _reads++;
}";

        private static string Service(string types, string result = "List<Order>") => $@"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

namespace App
{{
{types}
    public interface IOrders
    {{
        [Cache] Task<{result}> GetAsync(int customerId);
    }}
}}";

        private static string Shape(string source, params Microsoft.CodeAnalysis.MetadataReference[] references)
            => Regex.Match(Run(Compilation("App", source, references)).Source("GeneratedPolicyRegistrations.g.cs"), "ResultShape = \"([0-9a-f]{16})\"").Groups[1].Value;

        [Theory]
        [InlineData("public int Id { get; set; }\n    public Status Status { get; set; }", "public Status Status { get; set; }\n    public int Id { get; set; }")]
        [InlineData("public int Reads() => _reads++;", "public int Reads() => _reads += 2;")]
        [InlineData("private int _reads;", "private int _reads; private string _note = \"\";")]
        [InlineData("public int Reads() => _reads++;", "public int Reads() => _reads++; public int Twice => Id * 2;")]
        public void AnEditASerializerDoesntSee_KeepsTheShape(string from, string to)
        {
            Assert.Matches("^[0-9a-f]{16}$", Shape(Service(Before)));
            Assert.Equal(Shape(Service(Before)), Shape(Service(Before.Replace(from, to))));
        }

        [Theory]
        [InlineData("public int Id { get; set; }", "public int OrderId { get; set; }")]
        [InlineData("public int Id { get; set; }", "public long Id { get; set; }")]
        [InlineData("public int Quantity { get; set; }", "public decimal Quantity { get; set; }")]
        [InlineData("Open = 1, Closed = 2", "Closed = 1, Open = 2")]
        [InlineData("Open = 1, Closed = 2", "Open = 1, Closed = 2, Cancelled = 3")]
        [InlineData("public int Id { get; set; }", "[System.Text.Json.Serialization.JsonPropertyName(\"id\")] public int Id { get; set; }")]
        [InlineData("public int Id { get; set; }", "[System.Text.Json.Serialization.JsonIgnore] public int Id { get; set; }")]
        [InlineData("private int _reads;", "[System.Text.Json.Serialization.JsonInclude] private int _reads;")]
        public void AChangeToWhatASerializerWrites_ChangesTheShape(string from, string to)
        {
            Assert.NotEqual(Shape(Service(Before)), Shape(Service(Before.Replace(from, to))));
        }

        private const string Zoo = @"
public class Animal { public string Name { get; set; } = """"; }";

        [Fact]
        public void ADerivedTypeAddedOrChanged_ChangesTheShapeOfAResultThatMayHoldIt()
        {
            var alone = Shape(Service(Zoo, "List<Animal>"));
            var withDog = Shape(Service(Zoo + "\npublic class Dog : Animal { public string Breed { get; set; } = \"\"; }", "List<Animal>"));
            var dogChanged = Shape(Service(Zoo + "\npublic class Dog : Animal { public int Breed { get; set; } }", "List<Animal>"));

            Assert.NotEqual(alone, withDog);
            Assert.NotEqual(withDog, dogChanged);
        }

        [Fact]
        public void ADerivedTypeInAReferencedAssembly_CountsToo()
        {
            string Domain(string dog) => "namespace Domain { public class Animal { public string Name { get; set; } = \"\"; } " + dog + " }";
            const string Contracts = @"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;
namespace App { public interface IZoo { [Cache] Task<List<Domain.Animal>> AnimalsAsync(); } }";

            var before = Shape(Contracts, Image(Compilation("Domain", Domain("public class Dog : Animal { public string Breed { get; set; } = \"\"; }"))));
            var after = Shape(Contracts, Image(Compilation("Domain", Domain("public class Dog : Animal { public int Breed { get; set; } }"))));

            Assert.NotEqual(before, after);
        }
    }
}
