using MethodCache.Infrastructure;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class RetentionPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LateLoadOrWrite_AfterTagOrClearStampExpires_ColdNodeCannotReadOldValue(bool clear, bool slowBackend)
    {
        var clock = new FakeTimeProvider();
        var backend = new ExpiringAtomicCache(clock);
        ServiceProvider Node()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clock);
            services.AddMethodCache();
            services.AddMethodCacheDistributedCache(_ => backend, options =>
            {
                options.TagRetention = TimeSpan.FromSeconds(1);
                options.ClockSkewTolerance = TimeSpan.Zero;
                options.TagCheckInterval = TimeSpan.Zero;
                options.OperationTimeout = TimeSpan.FromMinutes(1);
            });
            return services.BuildServiceProvider();
        }
        using var a = Node();
        using var b = Node();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (slowBackend) backend.BeforeWrite = async key =>
        {
            if (!key.EndsWith("e:slow", StringComparison.Ordinal)) return;
            writeBegan.TrySetResult();
            await writeFinish.Task;
        };
        async Task<string> Factory()
        {
            began.TrySetResult();
            await finish.Task;
            return "old";
        }
        var policy = CacheRuntimePolicy.Empty("probe") with { Duration = TimeSpan.FromMinutes(1), Tags = ["users"] };
        var load = a.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("slow", "probe", Factory, policy);
        await began.Task;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        if (clear) await b.GetRequiredService<InMemoryCacheManager>().ClearAsync();
        else await b.GetRequiredService<ICacheManager>().InvalidateByTagsAsync("users");
        clock.Advance(slowBackend ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(2));
        finish.SetResult();
        if (slowBackend)
        {
            await writeBegan.Task;
            clock.Advance(TimeSpan.FromMilliseconds(501));
            writeFinish.SetResult();
        }
        Assert.Equal("old", await load);
        backend.BeforeWrite = null;
        using var c = Node();

        var current = await c.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("slow", "probe", () => Task.FromResult("new"), policy);

        Assert.Equal("new", current);
    }
}
