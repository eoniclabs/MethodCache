using Xunit;
using static MethodCache.SourceGenerator.Tests.SourceGenerator.GeneratorRun;

namespace MethodCache.SourceGenerator.Tests.SourceGenerator
{
    /// <summary>
    /// Results a distributed cache can't bring back as they were: a type whose state serializers don't write (MC0042),
    /// and entities EF Core's lazy-loading proxies create (MC0041).
    /// </summary>
    public class ResultShapeDiagnosticsTests
    {
        private static string Service(string result, string types) => $@"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache;

namespace App
{{
{types}
    public interface IService
    {{
        [Cache] Task<{result}> GetAsync(int id);
    }}
}}";

        [Theory]
        [InlineData("Money", "public readonly struct Money { private readonly decimal _amount; public Money(decimal amount) => _amount = amount; public decimal Rounded() => _amount; }")]
        [InlineData("List<Order>", "public sealed class Order { public int Id { get; set; } public Money Total { get; set; } } public sealed class Money { private decimal _amount; public void Set(decimal a) => _amount = a; }")]
        [InlineData("Counter", "public sealed class Counter { private int Count { get; set; } public void Add() => Count++; }")]
        public void AResultWhoseStateIsAllNonPublic_GetsMC0042(string result, string types)
        {
            var run = Run(Compilation("App", Service(result, types)));

            var warning = Assert.Single(run.Diagnostics, d => d.Id == "MC0042");
            Assert.Contains("keeps its state in non-public fields or properties only", warning.GetMessage(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Money", "public readonly struct Money { public Money(decimal amount) => Amount = amount; public decimal Amount { get; } }")]
        [InlineData("Money", "public sealed class Money { [System.Text.Json.Serialization.JsonInclude] private decimal _amount; }")]
        [InlineData("Money", "[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))] public sealed class Money { private decimal _amount; }")]
        [InlineData("Empty", "public sealed class Empty { public static int Shared; }")]
        [InlineData("Money", "public sealed class Money { private readonly decimal _amount; public decimal Amount => _amount; }")]
        public void AResultThatWritesItsStateOrHasNone_GetsNoMC0042(string result, string types)
        {
            var run = Run(Compilation("App", Service(result, types)), allowErrors: true);

            Assert.DoesNotContain(run.Diagnostics, d => d.Id == "MC0042");
        }

        [Fact]
        public void ATypeFromAnotherAssembly_GetsNoMC0042_SinceTheCheckBeforeSharingJudgesIt()
        {
            // The build sees the private fields of a struct from metadata but not of a class, nor a converter in the
            // options passed to UseJson
            var package = Image(Compilation("Package", @"namespace Package
{
    public readonly struct Timestamp { private readonly long _ticks; public Timestamp(long ticks) => _ticks = ticks; public long Ticks() => _ticks; }
    public sealed class Moment { private readonly long _ticks; public Moment(long ticks) => _ticks = ticks; public long Ticks() => _ticks; }
}"));

            foreach (var member in new[] { "Package.Timestamp", "Package.Moment" })
            {
                var run = Run(Compilation("App", Service("Order", $"public sealed record Order(int Id, {member} CreatedAt);"), package));
                Assert.DoesNotContain(run.Diagnostics, d => d.Id == "MC0042");
            }
        }

        [Fact]
        public void AMethodWithDistributedFalse_GetsNoChecksOfTheDistributedCopy()
        {
            // Its values stay in memory, so no serializer writes them
            const string types = "public sealed class Money { private decimal _amount; public void Set(decimal a) => _amount = a; }";
            var memoryOnly = Service("Money", types).Replace("[Cache]", "[Cache(Distributed = false)]", StringComparison.Ordinal);
            var proxies = Image(Compilation("Microsoft.EntityFrameworkCore.Proxies", "namespace Microsoft.EntityFrameworkCore { public static class ProxiesExtensions { } }"));

            Assert.Contains(Run(Compilation("App", Service("Money", types))).Diagnostics, d => d.Id == "MC0042");
            Assert.DoesNotContain(Run(Compilation("App", memoryOnly)).Diagnostics, d => d.Id == "MC0042");
            Assert.DoesNotContain(Run(Compilation("App", Service("object", "").Replace("[Cache]", "[Cache(Distributed = false)]", StringComparison.Ordinal))).Diagnostics, d => d.Id == "MC0020");
            Assert.DoesNotContain(Run(Compilation("App", Service("List<Customer>", Entities).Replace("[Cache]", "[Cache(Distributed = false)]", StringComparison.Ordinal), proxies)).Diagnostics, d => d.Id == "MC0041");
        }

        private const string Entities = @"
    public class Customer { public int Id { get; set; } public virtual List<Order> Orders { get; set; } = new(); }
    public class Order { public int Id { get; set; } public virtual Customer? Customer { get; set; } }
    public sealed record CustomerDto(int Id, string Name);";

        [Fact]
        public void AnEntityWithVirtualNavigations_InAProjectWithLazyLoadingProxies_GetsMC0041()
        {
            var proxies = Image(Compilation("Microsoft.EntityFrameworkCore.Proxies", "namespace Microsoft.EntityFrameworkCore { public static class ProxiesExtensions { } }"));

            var run = Run(Compilation("App", Service("List<Customer>", Entities), proxies));

            var warning = Assert.Single(run.Diagnostics, d => d.Id == "MC0041");
            Assert.Contains("which holds 'Customer', an entity with virtual navigation properties", warning.GetMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public void AnEntityWithoutLazyLoadingProxies_OrAProjectedRecord_GetsNoMC0041()
        {
            var proxies = Image(Compilation("Microsoft.EntityFrameworkCore.Proxies", "namespace Microsoft.EntityFrameworkCore { public static class ProxiesExtensions { } }"));

            Assert.DoesNotContain(Run(Compilation("App", Service("List<Customer>", Entities))).Diagnostics, d => d.Id == "MC0041");
            Assert.DoesNotContain(Run(Compilation("App", Service("List<CustomerDto>", Entities), proxies)).Diagnostics, d => d.Id == "MC0041");
        }
    }
}
