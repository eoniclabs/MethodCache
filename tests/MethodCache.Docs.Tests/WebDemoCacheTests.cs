using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using MethodCache.WebDemo;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class WebDemoCacheTests
{
    [Fact]
    public async Task RepeatedAndConcurrentReads_ExecuteOneDatabaseQueryPerColdKey()
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        Assert.Equal(await client.GetFromJsonAsync<Product>("/products/1"), await client.GetFromJsonAsync<Product>("/products/1"));
        Assert.Equal(1, factory.Services.GetRequiredService<ProductCatalog>().Queries);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetFromJsonAsync<Product>("/products/3")));

        Assert.All(results, product => Assert.Equal(results[0], product));
        Assert.Equal(2, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(await client.GetFromJsonAsync<Product[]>("/products?category=keyboards"), await client.GetFromJsonAsync<Product[]>("/products?category=keyboards"));
        Assert.Equal(3, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        var stats = await client.GetFromJsonAsync<Stats>("/methodcache/stats");
        Assert.Equal(3, stats!.Misses);
        Assert.Equal(21, stats.Hits);
    }

    [Fact]
    public async Task ConcurrentColdReads_WhileDatabaseIsStillLoading_ShareOneLoad()
    {
        await using var factory = new DemoWebFactory(queryDelayMs: 300);
        using var client = factory.CreateClient();
        var reads = Enumerable.Range(0, 20).Select(_ => client.GetFromJsonAsync<Product>("/products/3")).ToArray();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (factory.Services.GetRequiredService<ProductCatalog>().Queries == 0)
        {
            guard.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.All(reads, read => Assert.False(read.IsCompleted));
        factory.Clock.Advance(TimeSpan.FromMilliseconds(300));
        var results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, product => Assert.Equal(results[0], product));
        Assert.Equal(1, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        var stats = await client.GetFromJsonAsync<Stats>("/methodcache/stats");
        Assert.Equal(1, stats!.Misses);
        Assert.Equal(19, stats.Hits);
    }

    [Fact]
    public async Task PriceChange_RefreshesProductAndLists_WhileOtherProductsStayCached()
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        await client.GetFromJsonAsync<Product>("/products/1");
        var unaffected = await client.GetFromJsonAsync<Product>("/products/2");
        await client.GetFromJsonAsync<Product[]>("/products?category=keyboards");
        using var changed = await client.PutAsJsonAsync("/products/1/price", new { price = 119m });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(unaffected, await client.GetFromJsonAsync<Product>("/products/2"));
        Assert.Equal(3, factory.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(119m, (await client.GetFromJsonAsync<Product>("/products/1"))!.Price);
        Assert.Equal(119m, (await client.GetFromJsonAsync<Product[]>("/products?category=keyboards"))!.Single(p => p.Id == 1).Price);
        Assert.Equal(5, factory.Services.GetRequiredService<ProductCatalog>().Queries);
    }

    [Fact]
    public async Task Reset_RestoresSeedDataAndInvalidatesCachedProducts()
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        using var changed = await client.PutAsJsonAsync("/products/3/price", new { price = 1m });
        Assert.Equal(1m, (await client.GetFromJsonAsync<Product>("/products/3"))!.Price);
        using var reset = await client.PostAsync("/demo/reset", null);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(79m, (await client.GetFromJsonAsync<Product>("/products/3"))!.Price);
        Assert.Equal(1, factory.Services.GetRequiredService<ProductCatalog>().Queries);
    }

    [Fact]
    public async Task SharedDatabase_ConcurrentWritesRetainBothProducts_AndSurviveRestart()
    {
        await using var first = new DemoWebFactory();
        await using var second = new DemoWebFactory(dataFile: first.DataFile);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        await a.GetAsync("/products/1");
        await b.GetAsync("/products/2");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Update(HttpClient client, int id, decimal price)
        {
            await start.Task;
            using var response = await client.PutAsJsonAsync($"/products/{id}/price", new { price });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        var writes = Task.WhenAll(Task.Run(() => Update(a, 1, 42m)), Task.Run(() => Update(b, 2, 43m)));
        start.SetResult();
        await writes;

        await using var restarted = new DemoWebFactory(dataFile: first.DataFile);
        using var reader = restarted.CreateClient();
        Assert.Equal(42m, (await reader.GetFromJsonAsync<Product>("/products/1"))!.Price);
        Assert.Equal(43m, (await reader.GetFromJsonAsync<Product>("/products/2"))!.Price);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpRequestFile_EveryStepSucceeds_AndProductTwoRemainsCached(bool windowsLineEndings)
    {
        await using var factory = new DemoWebFactory();
        using var client = factory.CreateClient();
        var text = await File.ReadAllTextAsync(Path.Combine(DemoWebFactory.RepositoryRoot, "samples", "MethodCache.WebDemo", "MethodCache.WebDemo.http"));
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (windowsLineEndings) text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var requests = 0;
        foreach (var block in Regex.Split(text, "^### ", RegexOptions.Multiline).Skip(1))
        {
            var match = Regex.Match(block, "^(GET|POST|PUT|DELETE) \\{\\{host\\}\\}([^\\r\\n]+)", RegexOptions.Multiline);
            if (!match.Success) continue;
            var before = factory.Services.GetRequiredService<ProductCatalog>().Queries;
            using var request = new HttpRequestMessage(new HttpMethod(match.Groups[1].Value), match.Groups[2].Value);
            var json = Regex.Match(block, "^\\{[^\\r\\n]*\\}\\r?$", RegexOptions.Multiline);
            if (json.Success) request.Content = new StringContent(json.Value.Trim(), System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"{block.Split('\n')[0]}: {response.StatusCode}");
            if (block.StartsWith("6.", StringComparison.Ordinal)) Assert.Equal(before, factory.Services.GetRequiredService<ProductCatalog>().Queries);
            requests++;
        }
        Assert.True(requests >= 12);
    }

    private sealed record Stats(long Hits, long Misses);
}
