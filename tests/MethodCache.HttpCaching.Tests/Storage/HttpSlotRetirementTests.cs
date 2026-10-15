using MethodCache.Core.Runtime.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class HttpSlotRetirementTests
{
    [Fact]
    public async Task InvalidateCollidingSlot_ReleasesEveryRetiredLocalBody_AndPreservesOtherSlots()
    {
        var time = new FakeTimeProvider();
        using var owner = new TestHttpStore(time);
        var core = owner.Core;
        var seen = new Dictionary<int, string>();
        string? first = null, second = null;
        for (var i = 0; i <= HttpResponseStore.GenerationSlots; i++)
        {
            var candidate = "url:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var slot = HttpResponseStore.Slot(candidate);
            if (seen.TryGetValue(slot, out first)) { second = candidate; break; }
            seen.Add(slot, candidate);
        }
        Assert.NotNull(first); Assert.NotNull(second);
        var third = seen.First(pair => pair.Key != HttpResponseStore.Slot(first)).Value;
        string[] tags = ["shared"];
        var before = await core.CaptureAsync(first, tags, default);
        Assert.Equal(before, await core.CaptureAsync(second, tags, default));
        var unaffected = await core.CaptureAsync(third, tags, default);
        foreach (var target in new[] { first, second, third })
        {
            var info = new HttpStorageInfo(HttpResponseStore.Identity(target), target, "test", core.Format, 1024,
                tags, time.GetUtcNow(), time.GetUtcNow() + TimeSpan.FromMinutes(1), false);
            Assert.True(core.StoreLocal(target, target, info));
        }
        Assert.Equal(3, core.List().Count);

        await core.InvalidateUrlsAsync([first], default);

        Assert.Null(core.ReadLocal(first));
        Assert.Null(core.ReadLocal(second)); // its old namespace was retired by the same slot advance
        Assert.Equal(third, core.ReadLocal(third));
        Assert.Single(core.List());
        Assert.NotEqual(before, await core.CaptureAsync(second, tags, default));
        Assert.Equal(unaffected, await core.CaptureAsync(third, tags, default));
    }
}
