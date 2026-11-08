using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace MethodCache.Analyzers.Tests.Analyzers
{
    public class ChangedResultAnalyzerTests
    {
        private const string Header = @"using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache;

[assembly: CacheInterface(typeof(IExternal), ""AllAsync"")]

public enum Status { Open, Closed }
public struct Money { public decimal Amount; }
public sealed class Line { public int Quantity { get; set; } }
public sealed class Order { public Status Status { get; set; } public List<Line> Lines { get; set; } = new(); public Money Total; public int Reads; }
public sealed record Summary(int Id, string Name);

public interface IOrders
{
    [Cache] Task<List<Order>> ForCustomerAsync(int customerId);
    [Cache] Order Get(int id);
    [Cache] Task<Dictionary<int, Order>> ByIdAsync();
    [Cache] Task<Order[]> RecentAsync();
    [Cache] Task<Summary> SummaryAsync(int id);
    [Cache] Task<Money> TotalAsync(int id);
    [Cache] Task<float[]> EmbeddingAsync(string text);
    Task<List<Order>> UncachedAsync();
}

public interface IExternal { Task<List<Order>> AllAsync(); Task<List<Order>> FreshAsync(); }
";

        [Theory]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); orders.Add(new Order());", "IOrders.ForCustomerAsync")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); orders.Sort((a, b) => 0);", "IOrders.ForCustomerAsync")]
        [InlineData("var order = _orders.Get(1); order.Status = Status.Closed;", "IOrders.Get")]
        [InlineData("var order = _orders.Get(1); order.Reads++;", "IOrders.Get")]
        [InlineData("var order = _orders.Get(1); order.Reads += 2;", "IOrders.Get")]
        [InlineData("var order = _orders.Get(1); order.Lines.Clear();", "IOrders.Get")]
        [InlineData("var order = _orders.Get(1); order.Total.Amount = 3;", "IOrders.Get")]
        [InlineData("foreach (var order in await _orders.ForCustomerAsync(1)) order.Status = Status.Closed;", "IOrders.ForCustomerAsync")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); foreach (var order in orders.Where(o => o.Reads > 0)) order.Lines[0].Quantity = 0;", "IOrders.ForCustomerAsync")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); orders.First().Status = Status.Closed;", "IOrders.ForCustomerAsync")]
        [InlineData("var copy = (await _orders.ForCustomerAsync(1)).ToList(); copy[0].Status = Status.Closed;", "IOrders.ForCustomerAsync")]
        [InlineData("var byId = await _orders.ByIdAsync(); byId.Remove(1);", "IOrders.ByIdAsync")]
        [InlineData("var byId = await _orders.ByIdAsync(); byId[1] = new Order();", "IOrders.ByIdAsync")]
        [InlineData("var recent = await _orders.RecentAsync(); recent[0] = new Order();", "IOrders.RecentAsync")]
        [InlineData("var recent = await _orders.RecentAsync(); System.Array.Reverse(recent);", "IOrders.RecentAsync")]
        [InlineData("(await _orders.ForCustomerAsync(1)).Add(new Order());", "IOrders.ForCustomerAsync")]
        [InlineData("var all = await _external.AllAsync(); all.Clear();", "IExternal.AllAsync")]
        [InlineData("var list = await _cache.GetOrCreateAsync(\"k\", _ => ValueTask.FromResult(new List<Order>())); list.Add(new Order());", "ICacheManager.GetOrCreateAsync")]
        [InlineData("var order = await _cache.GetOrCreateAsync(\"k\", _ => ValueTask.FromResult(new Order())); order.Status = Status.Closed;", "ICacheManager.GetOrCreateAsync")]
        [InlineData("var many = await _cache.GetOrCreateManyAsync(new[] { \"a\" }, (keys, _) => ValueTask.FromResult<IReadOnlyDictionary<string, Order>>(new Dictionary<string, Order>())); many[\"a\"].Status = Status.Closed;", "ICacheManager.GetOrCreateManyAsync")]
        [InlineData("var found = await _cache.TryGetAsync<List<Order>>(\"k\"); found.Value.Clear();", "ICacheManager.TryGetAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); v.AsSpan().Clear();", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); Span<float> s = v; s[0] = 0;", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); Span<float> s = v; for (var i = 0; i < s.Length; i++) s[i] /= 2;", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); var s = new Span<float>(v); s.Fill(1);", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); v.AsSpan(1).Slice(1)[0] = 2;", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); v.AsSpan().Sort();", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); Array.Copy(new float[3], v, 3);", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); Buffer.BlockCopy(new float[3], 0, v, 0, 12);", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); new float[3].CopyTo(v, 0);", "IOrders.EmbeddingAsync")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); new float[3].AsSpan().CopyTo(v);", "IOrders.EmbeddingAsync")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); System.Runtime.InteropServices.CollectionsMarshal.AsSpan(orders)[0] = new Order();", "IOrders.ForCustomerAsync")]
        public async Task AChangeToACachedResult_IsReported(string body, string method)
        {
            var diagnostic = Assert.Single(await AnalyzeAsync(Header + Consumer(body)));

            Assert.Equal("MC0043", diagnostic.Id);
            Assert.StartsWith($"This changes a cached result of '{method}'.", diagnostic.GetMessage());
        }

        [Theory]
        [InlineData("var copy = new List<Order>(await _orders.ForCustomerAsync(1)); copy.Add(new Order());")]
        [InlineData("var copy = (await _orders.ForCustomerAsync(1)).ToList(); copy.RemoveAt(0);")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); orders = orders.ToList(); orders.Add(new Order());")]
        [InlineData("var summary = await _orders.SummaryAsync(1); var renamed = summary with { Name = \"x\" };")]
        [InlineData("var total = await _orders.TotalAsync(1); total.Amount = 3;")]
        [InlineData("var orders = await _orders.UncachedAsync(); orders.Add(new Order());")]
        [InlineData("var fresh = await _external.FreshAsync(); fresh.Clear();")]
        [InlineData("var count = (await _orders.ForCustomerAsync(1)).Count; count++;")]
        [InlineData("var orders = await _orders.ForCustomerAsync(1); var names = orders.Select(o => new Order { Status = o.Status }).ToList(); names[0].Status = Status.Closed;")]
        [InlineData("var copy = new List<Order>(await _cache.GetOrCreateAsync(\"k\", _ => ValueTask.FromResult(new List<Order>()))); copy.Add(new Order());")]
        [InlineData("var many = await _cache.GetOrCreateManyAsync(new[] { \"a\" }, (keys, _) => ValueTask.FromResult<IReadOnlyDictionary<string, Order>>(new Dictionary<string, Order>())); var own = new Dictionary<string, Order>(many); own.Remove(\"a\");")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); var dest = new float[3]; Array.Copy(v, dest, 3);")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); v.CopyTo(new float[3], 0);")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); v.AsSpan().CopyTo(new float[3]);")]
        [InlineData("var v = await _orders.EmbeddingAsync(\"\"); ReadOnlySpan<float> r = v; var first = r[0];")]
        [InlineData("var v = (await _orders.EmbeddingAsync(\"\")).ToArray(); v.AsSpan().Clear();")]
        [InlineData("var v = (float[])(await _orders.EmbeddingAsync(\"\")).Clone(); Span<float> s = v; s[0] = 0;")]
        public async Task ACopyOrAnotherValue_IsNotReported(string body)
        {
            Assert.Empty(await AnalyzeAsync(Header + Consumer(body)));
        }

        [Fact]
        public async Task ATestProject_IsNotReported()
        {
            var source = Header + Consumer("var orders = await _orders.ForCustomerAsync(1); orders.Add(new Order());");

            Assert.Empty(await AnalyzeAsync(source, MetadataReference.CreateFromFile(typeof(FactAttribute).Assembly.Location)));
        }

        private static string Consumer(string body) => @"
public sealed class Consumer(IOrders _orders, IExternal _external, ICacheManager _cache)
{
    public async Task RunAsync()
    {
        " + body + @"
    }
}";

        private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params MetadataReference[] more)
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
                .Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(CacheAttribute).Assembly.Location))
                .Concat(more);
            var compilation = CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ChangedResultAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
        }
    }
}
