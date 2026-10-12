using MethodCache.Infrastructure;
using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

public class PublicationInvalidationTests
{
    [Theory]
    [InlineData("key", false)]
    [InlineData("tag", false)]
    [InlineData("clear", false)]
    [InlineData("key", true)]
    [InlineData("tag", true)]
    [InlineData("clear", true)]
    public async Task InvalidationDuringWeightCalculation_OldPublicationCannotOverwriteNewEntry(string invalidation, bool explicitWrite)
    {
        using var finishWeight = new ManualResetEventSlim();
        var weighing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheMemoryOptions
        {
            EnableBackgroundCleanup = false,
            Weigher = (_, value) =>
            {
                if (Equals(value, "old"))
                {
                    weighing.TrySetResult();
                    finishWeight.Wait();
                }
                return 1;
            }
        }), new FakeTimeProvider());
        var policy = CacheRuntimePolicy.Empty("publication") with { Duration = TimeSpan.FromMinutes(1), Tags = ["tag"] };
        var old = Task.Run(() => cache.GetOrCreateFastAsync("key", "publication", () => Task.FromResult("old"), policy));
        try
        {
            await weighing.Task;
            if (invalidation == "key") await cache.InvalidateByKeysAsync("key");
            else if (invalidation == "tag") await cache.InvalidateByTagsAsync("tag");
            else await cache.ClearAsync();
            if (explicitWrite) await cache.SetAsync("key", "new", TimeSpan.FromMinutes(1));
            else Assert.Equal("new", await cache.GetOrCreateFastAsync("key", "publication", () => Task.FromResult("new"), policy));
            finishWeight.Set();
            Assert.Equal("old", await old);
            Assert.True(cache.TryGetFast<string>("key", out var stored));
            Assert.Equal("new", stored);
        }
        finally
        {
            finishWeight.Set();
            await old;
        }
    }
}
