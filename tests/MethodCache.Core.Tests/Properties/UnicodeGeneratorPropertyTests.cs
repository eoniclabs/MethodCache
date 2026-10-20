using CsCheck;
using MethodCache.Core.Tests.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(CpuIntensiveCollection))]
public class UnicodeGeneratorPropertyTests
{
    public sealed record KeyPart(string CacheKeyPart) : ICacheKeyProvider;
    public sealed record TextHolder(string Value);
    private static readonly CacheRuntimePolicy Policy = CacheRuntimePolicy.Empty("unicode");

    [Theory]
    [InlineData(typeof(JsonKeyGenerator))]
    public void KeyProvider_LoneSurrogates_DoNotAlias(Type generatorType)
    {
        var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
        Assert.NotEqual(generator.GenerateKey("unicode", [new KeyPart("\ud800")], Policy),
            generator.GenerateKey("unicode", [new KeyPart("\ud801")], Policy));
    }

    [Theory]
    [InlineData(typeof(FastHashKeyGenerator))]
    [InlineData(typeof(JsonKeyGenerator))]
    public void KeyGenerators_PreserveOrdinalUtf16AcrossArgumentsProvidersAndNestedValues(Type generatorType)
    {
        var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
        var text = Gen.String[Gen.Frequency((1, Gen.Char), (2, Gen.Char["\0\ufffd\ud800\ud801\udfff\uffffabc"])), 0, 12];
        Gen.Select(text, text).Sample((a, b) =>
        {
            foreach (var wrap in new Func<string, object>[] { s => s, s => new KeyPart(s),
                s => new TextHolder(s), s => new object[] { s }, s => new Dictionary<string, string> { [s] = s } })
            {
                Assert.Equal(a == b, generator.GenerateKey("unicode", [wrap(a)], Policy) ==
                    generator.GenerateKey("unicode", [wrap(b)], Policy));
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }
}
