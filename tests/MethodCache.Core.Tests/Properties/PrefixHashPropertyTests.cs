using MethodCache.Policies;
using MethodCache.Core.Runtime.Http;
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
    public void HttpClientPrefix_ArbitraryUtf16Suffixes_PreservePermanentSlot()
    {
        Gen.Select(Gen.String[Gen.Char["abc:\\\0\ud800\udfffé"], 0, 100], Gen.String[Gen.Char["abc:\\\0\ud800\udfffé"], 0, 200]).Sample((prefix, suffix) =>
        {
            var binding = new HttpResponseStore.UrlSlotPrefix(prefix);
            var key = prefix + suffix;
            Assert.Equal(HttpResponseStore.Slot(key), binding.Slot(key, out var canonical));
            Assert.True(canonical);
            var other = "unbound:" + key;
            Assert.Equal(HttpResponseStore.Slot(other), binding.Slot(other, out _));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void PreparedFallbackPrefix_ArbitraryUtf16NamesAndVersions_PreservesExactExternalKeys()
    {
        var generator = new FastHashKeyGenerator();
        Gen.Select(Gen.String[Gen.Char["abc:<>,\\\0\ud800\udfffé"], 0, 160], Gen.Int, Gen.Int).Sample((method, version, value) =>
        {
            foreach (var selected in new int?[] { null, version })
            {
                var policy = CacheRuntimePolicy.Empty("property") with { Version = selected };
                var prefix = FastHashKeyGenerator.PreparePrefix(method, selected);
                object?[] arguments = [value, "text:\\0\ud800", null];
                Assert.Equal(generator.GenerateKey(method, arguments, policy), generator.GenerateKey(prefix, arguments));
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void DefaultBuilder_ToString_PreservesEmptyTextWithoutABinding()
    {
        Assert.Equal(string.Empty, default(KeyBuilder).ToString());
        Assert.Equal(ProcessKeyHash.Compute(string.Empty.AsSpan()), ProcessKeyHash.Compute(string.Empty));
    }

    [Fact]
    public void Marvin_MatchesPinnedDotNetReference_AtEveryByteSplit()
    {
        var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        foreach (var seed in new[] { 0UL, 1UL, ulong.MaxValue, 0x0706050403020100UL })
            for (var length = 0; length <= input.Length; length++)
            {
                var expected = Reference.Marvin.ComputeHash32(input.AsSpan(0, length), seed);
                for (var split = 0; split <= length; split++)
                {
                    var hash = new MarvinState(seed);
                    hash.Append(input.AsSpan(0, split));
                    hash.Append(input.AsSpan(split, length - split));
                    Assert.Equal(expected, hash.Finish32());
                    Assert.Equal(expected, hash.Finish32());
                }
            }
    }

    [Fact]
    public void SplitUtf16_WithArbitrarySeedsAndSurrogates_EqualsWholeText()
    {
        Gen.Select(Gen.Long, Gen.Long, Gen.String[Gen.Char["a:\\\0\ud800\udfffé"], 0, 200]).Sample((key0, key1, text) =>
        {
            var full = new MarvinState(unchecked((ulong)key0) ^ unchecked((ulong)key1));
            full.AppendUtf16(text);
            for (var split = 0; split <= text.Length; split++)
            {
                var parts = new MarvinState(unchecked((ulong)key0) ^ unchecked((ulong)key1));
                parts.AppendUtf16(text.AsSpan(0, split));
                parts.AppendUtf16(ReadOnlySpan<char>.Empty);
                var prefix = parts;
                Assert.Equal(full.Finish32(), prefix.FinishUtf16(text.AsSpan(split)));
                parts.AppendUtf16(text.AsSpan(split));
                Assert.Equal(full.Finish32(), parts.Finish32());
                Assert.Equal(prefix.Finish32(), prefix.Finish32());
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
            var key = new KeyBuilder(call, new char[capacity]);
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
    public void AdversarialPolynomialCollisionFamily_DoesNotCreateAQuadraticMarvinProbeWorkload()
    {
        // Every equal-length Aa/BB composition collides under a seeded base-31 polynomial, for every seed.
        // Exercise that chosen-input family with long common prefixes, NUL and an isolated surrogate.
        var prefix = new string('p', 512) + "\0\ud800";
        var keys = Enumerable.Range(0, 4096).Select(value => prefix + string.Concat(
            Enumerable.Range(0, 12).Select(bit => (value & (1 << bit)) == 0 ? "Aa" : "BB"))).ToArray();
        Assert.Single(keys.Select(key => Polynomial(key)).Distinct());
        foreach (var seed in new[] { (0UL, 0UL), (1UL, ulong.MaxValue), (0x0706050403020100UL, 0x0f0e0d0c0b0a0908UL) })
        {
            var prefixState = new MarvinState(seed.Item1 ^ seed.Item2);
            prefixState.AppendUtf16(prefix);
            var hashes = new List<int>(keys.Length);
            foreach (var key in keys)
            {
                var split = prefixState;
                split.AppendUtf16(key.AsSpan(prefix.Length));
                var whole = new MarvinState(seed.Item1 ^ seed.Item2);
                whole.AppendUtf16(key);
                Assert.Equal(whole.Finish32(), split.Finish32());
                hashes.Add(split.Finish32());
            }
            var chains = hashes.GroupBy(hash => hash).Select(group => group.Count()).ToArray();
            // Visiting every node in every equal-hash chain costs sum(n^2) equality probes. This bounded,
            // deterministic work metric avoids fragile nanosecond assertions; it is additional regression
            // evidence, not a substitute for the randomized-comparer security review and pinned reference oracle.
            var comparisons = chains.Sum(length => (long)length * length);
            Assert.InRange(comparisons, keys.Length, 2L * keys.Length);
            Assert.InRange(chains.Max(), 1, 3);
        }
    }

    [Fact]
    public void ImmutableStringMemo_InterleavedOwnersAndLargeKeys_AlwaysMatchesWholeTextHash()
    {
        var keys = Enumerable.Range(0, 100).Select(i => new string('x', i % 2 == 0 ? 4096 : 40) + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        Parallel.For(0, 8, _ =>
        {
            foreach (var key in keys)
            {
                var expected = ProcessKeyHash.Compute(key.AsSpan());
                Assert.Equal(expected, ProcessKeyHash.Compute(key));
                Assert.Equal(expected, ProcessKeyHash.Compute(key));
                var independent = new string(key.ToCharArray());
                Assert.Equal(expected, ProcessKeyHash.Compute(independent));
            }
        });
    }

    private static uint Polynomial(string value)
    {
        var hash = 12345u;
        foreach (var character in value) hash = unchecked(hash * 31 + character);
        return hash;
    }

    private interface IService { }
}
