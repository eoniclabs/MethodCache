using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using CsCheck;
using MethodCache.Core.Runtime.Http;
using MethodCache.Core.Telemetry;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MsOptions = Microsoft.Extensions.Options.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class HttpStorageIdentityPropertyTests
{
    [Fact]
    public void LocalDeferredMetadata_ArbitraryUtf16Prefix_PreservesIdentityAndRemoval()
    {
        var time = new FakeTimeProvider();
        Gen.Select(Gen.Int[char.MinValue, char.MaxValue].Array[0, 80], Gen.Int[char.MinValue, char.MaxValue].Array[0, 80]).Sample((prefixUnits, suffixUnits) =>
        {
            using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            using var telemetry = new CoreTelemetry(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                MsOptions.Create(new MethodCacheTelemetryOptions()), time);
            using var store = new HttpResponseStore(MsOptions.Create(new MethodCacheMemoryOptions()), telemetry, time);
            var prefix = new string(prefixUnits.Select(unit => (char)unit).ToArray());
            var key = prefix + new string(suffixUnits.Select(unit => (char)unit).ToArray());
            var now = time.GetUtcNow();
            var info = new HttpStorageInfo(string.Empty, string.Empty, "client", 2, 100, [], now,
                now + TimeSpan.FromMinutes(1), false, prefix.Length, HttpResponseStore.Slot(prefix));
            Assert.True(store.StoreLocal(key, "payload", info));
            var observed = Assert.Single(store.List());
            Assert.Equal(prefix, observed.UrlTarget);
            Assert.Equal(HttpResponseStore.Identity(key), observed.Id);
            Assert.Same(observed, Assert.Single(store.List()));
            Assert.True(store.RemoveObservationAsync(observed.Id, default).GetAwaiter().GetResult());
            Assert.Null(store.ReadLocal(key));
            Assert.Empty(store.List());
        }, seed: PropertySettings.Seed, iter: PropertySettings.ModelIterations);
    }

    [Fact]
    public void ObservationIdentity_ArbitraryUtf16_MatchesFrozenBytesAndPortableStreaming()
    {
        Gen.Int[char.MinValue, char.MaxValue].Array[0, 1024].Sample(units =>
        {
            var key = new string(units.Select(unit => (char)unit).ToArray());
            var encoded = new byte[units.Length * 2];
            for (var i = 0; i < units.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(i * 2), (ushort)units[i]);
            var expected = SHA256.HashData(encoded);
            Assert.Equal("http:" + Convert.ToHexString(expected), HttpResponseStore.Identity(key));
            Span<byte> streamed = stackalloc byte[32];
            HttpResponseStore.HashIdentityUtf16Streaming(key, streamed);
            Assert.Equal(expected, streamed.ToArray());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void VectorIdentity_ArbitraryComponents_MatchesFrozenFraming()
    {
        // Up to 6 addresses of 300 code units cover both the stack and the pooled frame buffer
        Gen.Select(Gen.Int[char.MinValue, char.MaxValue].Array[0, 300], Gen.Guid, Gen.ULong).Array[1, 6].Sample(components =>
        {
            var addresses = components.Select(c => new string(c.Item1.Select(unit => (char)unit).ToArray())).ToArray();
            var epochs = components.Select(c => new CacheEpoch(c.Item2, c.Item3)).ToArray();
            using var frames = new MemoryStream();
            for (var i = 0; i < addresses.Length; i++)
            {
                var frame = new byte[4 + addresses[i].Length * 2 + 24];
                BinaryPrimitives.WriteInt32LittleEndian(frame, addresses[i].Length * 2);
                for (var j = 0; j < addresses[i].Length; j++) BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4 + j * 2), addresses[i][j]);
                epochs[i].Initialization.TryWriteBytes(frame.AsSpan(frame.Length - 24));
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(frame.Length - 8), epochs[i].Revision);
                frames.Write(frame);
            }
            Assert.Equal(Convert.ToHexString(SHA256.HashData(frames.ToArray())), HttpResponseStore.VectorIdentity(addresses, epochs));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void VectorIdentity_PooledFrames_AreClearedBeforeReuse()
    {
        // Two 600-unit addresses need 2 456 frame bytes, more than the stack buffer holds
        string[] addresses = [new string('a', 600), new string('b', 600)];
        CacheEpoch[] epochs = [new(Guid.NewGuid(), 1), new(Guid.NewGuid(), 2)];
        _ = HttpResponseStore.VectorIdentity(addresses, epochs);

        // The shared pool hands this thread the array it returned last for that size
        var reused = ArrayPool<byte>.Shared.Rent(2 * (4 + 1200 + 24));
        try
        {
            Assert.DoesNotContain(reused, b => b != 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(reused);
        }
    }
}
