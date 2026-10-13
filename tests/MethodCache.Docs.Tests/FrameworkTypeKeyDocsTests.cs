using System.Net;
using System.Numerics;
using System.Text.Json;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Serialization.MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>The framework types section of docs/features/keys.md, through a generated decorator.</summary>
public class FrameworkTypeKeyDocsTests
{
    [Fact]
    public async Task FrameworkTypes_AreKeyedByTheirValue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILookups, Lookups>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var lookups = provider.GetRequiredService<ILookups>();

        // 3 and 5 have the same IsZero, IsOne and Sign, and are still two entries
        Assert.Equal("3", lookups.Balance(new BigInteger(3)));
        Assert.Equal("5", lookups.Balance(new BigInteger(5)));

        // IPAddress.Loopback and IPAddress.Parse("127.0.0.1") share a key
        Assert.Equal(1, lookups.Origin(IPAddress.Loopback));
        Assert.Equal(1, lookups.Origin(IPAddress.Parse("127.0.0.1")));

        // A JsonElement is written as its JSON, so whitespace doesn't count
        Assert.Equal(1, lookups.Filter(Json("{\"a\":1}")));
        Assert.Equal(1, lookups.Filter(Json("{ \"a\": 1 }")));
        Assert.Equal(2, lookups.Filter(Json("{\"a\":2}")));
    }

    [Fact]
    public async Task AspNetCoreRequestValues_AreKeyedByTheirValue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequests, Requests>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var requests = provider.GetRequiredService<IRequests>();

        Assert.Equal("/a?x=1 example.com", requests.Describe(new PathString("/a"), new QueryString("?x=1"), new HostString("example.com")));
        Assert.Equal("/b?x=1 example.com", requests.Describe(new PathString("/b"), new QueryString("?x=1"), new HostString("example.com")));
        Assert.Equal("/a?x=2 example.com", requests.Describe(new PathString("/a"), new QueryString("?x=2"), new HostString("example.com")));
        Assert.Equal("/a?x=1 example.org", requests.Describe(new PathString("/a"), new QueryString("?x=1"), new HostString("example.org")));
    }

    [Theory]
    [InlineData(nameof(FastHashKeyGenerator))]
    [InlineData(nameof(JsonKeyGenerator))]
    [InlineData(nameof(MessagePackKeyGenerator))]
    public void AspNetCoreRequestValues_GetDifferentKeysInEveryKeyGenerator(string name)
    {
        ICacheKeyGenerator generator = name switch
        {
            nameof(FastHashKeyGenerator) => new FastHashKeyGenerator(),
            nameof(JsonKeyGenerator) => new JsonKeyGenerator(),
            _ => new MessagePackKeyGenerator(),
        };
        string Key(object value) => generator.GenerateKey("Requests", [value], CacheRuntimePolicy.Empty("Requests"));

        Assert.NotEqual(Key(new PathString("/a")), Key(new PathString("/b")));
        Assert.Equal(Key(new PathString("/a")), Key(new PathString("/a")));
        Assert.NotEqual(Key(new QueryString("?x=1")), Key(new QueryString("?x=2")));
        Assert.NotEqual(Key(new HostString("example.com", 80)), Key(new HostString("example.com", 81)));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public interface ILookups
    {
        [Cache] string Balance(BigInteger amount);
        [Cache] int Origin(IPAddress address);
        [Cache] int Filter(JsonElement filter);
    }

    public interface IRequests
    {
        [Cache] string Describe(PathString path, QueryString query, HostString host);
    }

    private sealed class Requests : IRequests
    {
        public string Describe(PathString path, QueryString query, HostString host) => $"{path}{query} {host}";
    }

    private sealed class Lookups : ILookups
    {
        private int _origins;
        private int _filters;

        public string Balance(BigInteger amount) => amount.ToString();
        public int Origin(IPAddress address) => ++_origins;
        public int Filter(JsonElement filter) => ++_filters;
    }
}
