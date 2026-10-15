using System.Buffers.Binary;
using System.Security.Cryptography;
using CsCheck;
using MethodCache.Core.Runtime.Http;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class HttpStorageIdentityPropertyTests
{
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
}
