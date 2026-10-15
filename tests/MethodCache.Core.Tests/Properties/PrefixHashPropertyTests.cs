using MethodCache.Policies;
using System.Globalization;
using CsCheck;
using MethodCache.Core.Runtime.Keys;
using MethodCache.Generated;
using MethodCache.Infrastructure;
using MethodCache.Extensibility;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class PrefixHashPropertyTests
{
    [Fact]
    public void SipHash24_MatchesAllPinnedAuthorReferenceVectors_AtEverySplit()
    {
        var vectors = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "siphash24-vectors.txt"))
            .Where(line => !line.StartsWith('#')).Select(line => ulong.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(64, vectors.Length);
        var input = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        for (var length = 0; length < 64; length++)
            for (var split = 0; split <= length; split++)
            {
                var hash = new SipHashState(0x0706050403020100, 0x0f0e0d0c0b0a0908);
                hash.Append(input.AsSpan(0, split));
                hash.Append(input.AsSpan(split, length - split));
                Assert.Equal(vectors[length], hash.Finish64());
                Assert.Equal(vectors[length], hash.Finish64()); // finalization leaves reusable state untouched
            }
    }

    [Fact]
    public void SplitUtf16_WithArbitrarySeedsAndSurrogates_EqualsWholeText()
    {
        Gen.Select(Gen.Long, Gen.Long, Gen.String[Gen.Char["a:\\\0\ud800\udfffé"], 0, 200]).Sample((key0, key1, text) =>
        {
            var full = new SipHashState(unchecked((ulong)key0), unchecked((ulong)key1));
            full.AppendUtf16(text);
            for (var split = 0; split <= text.Length; split++)
            {
                var parts = new SipHashState(unchecked((ulong)key0), unchecked((ulong)key1));
                parts.AppendUtf16(text.AsSpan(0, split));
                parts.AppendUtf16(ReadOnlySpan<char>.Empty);
                var prefix = parts;
                parts.AppendUtf16(text.AsSpan(split));
                Assert.Equal(full.Finish64(), parts.Finish64());
                Assert.Equal(prefix.Finish64(), prefix.Finish64());
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void Builder_GrowthEscapingAndAppendAfterLookup_PreserveWholeTextHash()
    {
        using var memory = new InMemoryCacheManager();
        var services = new DecoratorServices(memory, Substitute.For<IPolicyRegistry>(), new FastHashKeyGenerator());
        var method = new MethodCache.Generated.CachedMethod(services, PolicyHandle.Fixed(CacheRuntimePolicy.Empty("Get")), typeof(IService), "Get", "Get");
        var call = method.Capture();
        Gen.Select(Gen.Int, Gen.String[Gen.Char["a:\\\0\ud800\udfffé"], 0, 200], Gen.Int[0, 256]).Sample((number, text, capacity) =>
        {
            var key = call.StartKey(new char[capacity]);
            key.AppendPart(number);
            key.AppendPart(text);
            Assert.Equal(ProcessKeyHash.Compute(key.AsSpan()), key.LookupHash());
            var before = key.ToString();
            _ = call.TryGetValue<int>(ref key, out _);
            key.AppendPart(text);
            key.Append(":tail");
            Assert.Equal(ProcessKeyHash.Compute(key.AsSpan()), key.LookupHash());
            Assert.NotEqual(before, key.ToString());
            var fallback = call.BindKey(before);
            Assert.Equal(ProcessKeyHash.Compute(before), fallback.LookupHash());
            fallback.AppendPart(text);
            Assert.Equal(ProcessKeyHash.Compute(fallback.AsSpan()), fallback.LookupHash());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void AdversarialPolynomialCollisionFamily_DoesNotCreateAQuadraticSipHashProbeWorkload()
    {
        // Every equal-length Aa/BB composition collides under a seeded base-31 polynomial, for every seed.
        // Exercise that chosen-input family with long common prefixes, NUL and an isolated surrogate.
        var prefix = new string('p', 512) + "\0\ud800";
        var keys = Enumerable.Range(0, 4096).Select(value => prefix + string.Concat(
            Enumerable.Range(0, 12).Select(bit => (value & (1 << bit)) == 0 ? "Aa" : "BB"))).ToArray();
        Assert.Single(keys.Select(key => Polynomial(key)).Distinct());
        foreach (var seed in new[] { (0UL, 0UL), (1UL, ulong.MaxValue), (0x0706050403020100UL, 0x0f0e0d0c0b0a0908UL) })
        {
            var prefixState = new SipHashState(seed.Item1, seed.Item2);
            prefixState.AppendUtf16(prefix);
            var hashes = new List<int>(keys.Length);
            foreach (var key in keys)
            {
                var split = prefixState;
                split.AppendUtf16(key.AsSpan(prefix.Length));
                var whole = new SipHashState(seed.Item1, seed.Item2);
                whole.AppendUtf16(key);
                Assert.Equal(whole.Finish32(), split.Finish32());
                hashes.Add(split.Finish32());
            }
            var chains = hashes.GroupBy(hash => hash).Select(group => group.Count()).ToArray();
            // Visiting every node in every equal-hash chain costs sum(n^2) equality probes. This bounded,
            // deterministic work metric avoids fragile nanosecond assertions; it is additional regression
            // evidence, not a substitute for the PRF/security review and pinned reference vectors.
            var comparisons = chains.Sum(length => (long)length * length);
            Assert.InRange(comparisons, keys.Length, 2L * keys.Length);
            Assert.InRange(chains.Max(), 1, 3);
        }
    }

    private static uint Polynomial(string value)
    {
        var hash = 12345u;
        foreach (var character in value) hash = unchecked(hash * 31 + character);
        return hash;
    }

    private interface IService { }
}
