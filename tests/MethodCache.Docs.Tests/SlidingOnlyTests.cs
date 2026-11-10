using Docs.Expiration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// The "Kept as long as it is read" recipe in the expiration guide: a method with only a sliding window has no
/// five-minute end.
/// </summary>
public class SlidingOnlyTests
{
    private readonly FakeTimeProvider _time = new();

    private sealed class Layouts : ILayouts
    {
        public int Calls;

        public Task<Layout> GetAsync(string name) => Task.FromResult(new Layout($"{name}:{++Calls}"));
    }

    [Fact]
    public async Task OnlyASlidingWindow_KeepsTheEntryWhileItIsRead_AndDropsItAfterAWindowWithoutReads()
    {
        var source = new Layouts();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<ILayouts>(source);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var layouts = provider.GetRequiredService<ILayouts>();

        await layouts.GetAsync("home");
        for (var i = 0; i < 12; i++) // six hours of reads every 30 minutes, less a second
        {
            _time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
            await layouts.GetAsync("home");
        }

        Assert.Equal(1, source.Calls);

        _time.Advance(TimeSpan.FromMinutes(30));
        await layouts.GetAsync("home");
        Assert.Equal(2, source.Calls);
    }
}
