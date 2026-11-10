using Microsoft.Extensions.DependencyInjection;
using MyApp;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>The "Secrets in arguments" recipe in docs/features/keys.md.</summary>
public class SecretKeyTests
{
    private sealed class Blobs : IBlobReader
    {
        public int Calls;

        public Task<byte[]> ReadAsync(SignedUrl url) { Calls++; return Task.FromResult(new byte[] { 1 }); }
    }

    [Fact]
    public async Task ASignedUrl_IsKeyedByAHashOfTheWholeUrl_SoTheTokenNeverShowsAndEachTokenIsItsOwnEntry()
    {
        var blobs = new Blobs();
        var services = new ServiceCollection();
        services.AddSingleton<IBlobReader>(blobs);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IBlobReader>();
        var keys = provider.GetRequiredService<IMethodCacheKeys>();
        var first = new SignedUrl(new Uri("https://example.blob.core.windows.net/files/report.pdf?sv=2024&sig=first-secret"));
        var second = new SignedUrl(new Uri("https://example.blob.core.windows.net/files/report.pdf?sv=2024&sig=second-secret"));

        var key = keys.For<IBlobReader>(r => r.ReadAsync(first));
        Assert.DoesNotContain("first-secret", key);
        Assert.DoesNotContain("report.pdf", key);

        await reader.ReadAsync(first);
        await reader.ReadAsync(first);
        await reader.ReadAsync(second);
        Assert.Equal(2, blobs.Calls);
        var entries = provider.GetRequiredService<ICacheAdministrator>().ListEntries();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.DoesNotContain("secret", entry.StoredKey));
    }
}
