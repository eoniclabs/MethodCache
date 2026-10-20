using MethodCache.Core.Runtime.Http;
using MethodCache.HttpCaching.Internal;
using MethodCache.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpReviewRegressionTests
{
    [Theory]
    [InlineData("blank")]
    [InlineData("long")]
    [InlineData("many")]
    public async Task InvalidStaticTags_FailHostStartup(string invalid)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddHttpClient("invalid-tags").AddHttpCaching(options =>
        {
            if (invalid == "blank") options.Tags.Add(" ");
            else if (invalid == "long") options.Tags.Add(new string('t', 1025));
            else for (var index = 0; index < 126; index++) options.Tags.Add("tag-" + index);
        });
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("size")]
    [InlineData("size-zero")]
    [InlineData("default-age")]
    [InlineData("heuristic")]
    [InlineData("retention")]
    [InlineData("stale-error")]
    [InlineData("background-zero")]
    [InlineData("background-large")]
    public async Task InvalidSizeOrDuration_FailsHostStartup(string invalid)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddHttpClient("invalid-bounds").AddHttpCaching(options =>
        {
            switch (invalid)
            {
                case "size": options.MaxResponseBytes = -1; break;
                case "size-zero": options.MaxResponseBytes = 0; break;
                case "default-age": options.DefaultMaxAge = TimeSpan.FromTicks(-1); break;
                case "heuristic": options.MaxHeuristicFreshness = TimeSpan.FromTicks(-1); break;
                case "retention": options.StaleRetention = TimeSpan.FromTicks(-1); break;
                case "stale-error": options.MaxStaleIfError = TimeSpan.FromTicks(-1); break;
                case "background-zero": options.BackgroundRevalidationTimeout = TimeSpan.Zero; break;
                default: options.BackgroundRevalidationTimeout = TimeSpan.MaxValue; break;
            }
        });
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidZeroAndLargeBounds_StartAndStoreWithoutOverflow(bool large)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddHttpClient("valid-bounds").AddHttpCaching(options =>
        {
            options.MaxResponseBytes = large ? long.MaxValue : 1;
            options.DefaultMaxAge = large ? TimeSpan.MaxValue : TimeSpan.Zero;
            options.MaxHeuristicFreshness = large ? TimeSpan.MaxValue : TimeSpan.Zero;
            options.StaleRetention = large ? TimeSpan.MaxValue : TimeSpan.Zero;
            options.MaxStaleIfError = large ? TimeSpan.MaxValue : TimeSpan.Zero;
            options.BackgroundRevalidationTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        });
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        var time = new FakeTimeProvider();
        using var store = new TestHttpStore(time);
        await store.SetAsync("large", new HttpCacheEntry { RequestUri = "https://example.test/large", Method = "GET", StoredAt = time.GetUtcNow() }, TimeSpan.MaxValue);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(store.Core.List()).Deadline);
    }

    [Fact]
    public async Task ActiveInvalidation_DoesNotBlockAnUnrelatedSlot()
    {
        var time = new FakeTimeProvider();
        var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        const string target = "https://example.test/mutated";
        var other = "https://example.test/unrelated";
        while (HttpResponseStore.Slot(target) == HttpResponseStore.Slot(other)) other += "x";
        backend.HoldNextAdvance = true;
        var mutation = storage.Core.InvalidateUrlsAsync([target], default).AsTask();
        try
        {
            await backend.Started.Task;
            Assert.NotNull(await storage.Core.CaptureAsync(other, [], default));
            Assert.Null(await storage.Core.CaptureAsync(target, [], default));
        }
        finally { backend.Release.TrySetResult(); await mutation; }
    }

    [Fact]
    public async Task LongClientName_UsesBoundedDistinctIdentity()
    {
        var time = new FakeTimeProvider();
        using var storage = new TestHttpStore(time);
        var name = new string('a', 4096);
        var view = storage.ForClient(name, new HttpCacheOptions());
        var entry = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow() };
        await view.SetAsync("key:m:GET", entry, TimeSpan.FromMinutes(1));
        var info = Assert.Single(storage.Core.List());
        Assert.True(info.Client.Length <= 256);
        Assert.All(info.Tags, tag => Assert.True(tag.Length <= 1024));
        Assert.Contains(HttpCacheKeys.ClientIdentity(name), info.Client, StringComparison.Ordinal);
        var other = storage.ForClient(name + "b", new HttpCacheOptions());
        Assert.NotEqual(await view.GetGenerationAsync("key"), await other.GetGenerationAsync("key"));
    }

    [Fact]
    public async Task LongClientTag_CannotAliasAShortClientWithHashLookingName()
    {
        var time = new FakeTimeProvider();
        using var storage = new TestHttpStore(time);
        var longClient = storage.ForClient(new string('a', 4096), new HttpCacheOptions());
        var entry = new HttpCacheEntry { RequestUri = "https://example.test/a", Method = "GET", StoredAt = time.GetUtcNow() };
        await longClient.SetAsync("long:m:GET", entry, TimeSpan.FromMinutes(1));
        var longTag = Assert.Single(Assert.Single(storage.Core.List()).Tags);
        var shortClient = storage.ForClient(longTag[5..], new HttpCacheOptions());
        await shortClient.SetAsync("short:m:GET", entry, TimeSpan.FromMinutes(1));
        await storage.Core.InvalidateTagsAsync([longTag], default);
        Assert.Null(await longClient.GetAsync("long:m:GET"));
        Assert.Same(entry, await shortClient.GetAsync("short:m:GET"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedTagOrClear_ThrowsAndRetainsRecovery(bool clear)
    {
        var time = new FakeTimeProvider();
        var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        backend.FailStamp = true;
        var operation = clear ? storage.Core.ClearAsync(default).AsTask() : storage.Core.InvalidateTagsAsync(["users"], default).AsTask();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => operation);
        Assert.True(storage.Core.RecoveryQueues.Distributed > 0);
    }
}
