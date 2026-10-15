using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Generated;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using BoundMethod = MethodCache.Generated.CachedMethod;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class GeneratedKeyPropertyTests
{
    [Fact]
    public void Sequence_ArrayAndLazyValuesWithUnicode_MatchCanonicalHelper()
    {
        Gen.Select(Gen.Int[0, 40], Gen.String[Gen.Char["a:\\\0\ud800\udfffé"], 0, 100]).Sample((count, text) =>
        {
            var array = Enumerable.Range(0, count).Select(index => index % 3 == 0 ? null : text).ToArray();
            Assert.Equal(MethodCacheKeys.Sequence(array, static value => value!), KeyParts.Sequence(array, static value => value!));
            Assert.Equal(MethodCacheKeys.Sequence(array.Select(static value => value), static value => value!),
                KeyParts.Sequence(array.Select(static value => value), static value => value!));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
        Assert.Equal(MethodCacheKeys.Null, KeyParts.Sequence<string>(null, static value => value));
        Assert.Throws<ArgumentNullException>(() => KeyParts.Sequence<string>(null, null!));
    }

    [Fact]
    public void Sequence_ArrayFormatter_IsCalledOnceInOrderAndStopsOnFailure()
    {
        var visited = new List<int>();
        Assert.Equal("[3:1:2:3]", KeyParts.Sequence(new[] { 1, 2, 3 }, value =>
        {
            visited.Add(value);
            return MethodCacheKeys.Part(value);
        }));
        Assert.Equal(new[] { 1, 2, 3 }, visited);
        visited.Clear();
        Assert.Throws<InvalidOperationException>(() => KeyParts.Sequence(new[] { 1, 2, 3 }, value =>
        {
            visited.Add(value);
            if (value == 2) throw new InvalidOperationException();
            return MethodCacheKeys.Part(value);
        }));
        Assert.Equal(new[] { 1, 2 }, visited);
        Assert.Equal("[0]", KeyParts.Sequence(Array.Empty<int>(), _ => throw new InvalidOperationException()));
    }

    private interface IService { }
    private enum Number : long { Zero, Min = long.MinValue }

    [Fact]
    public void Builder_AllScalarFormsAndBufferSizes_MatchCanonicalHelperText()
    {
        using var memory = new InMemoryCacheManager(Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }));
        var services = new DecoratorServices(memory, Substitute.For<IPolicyRegistry>(), new FastHashKeyGenerator());
        var method = new BoundMethod(services, PolicyHandle.Fixed(CacheRuntimePolicy.Empty("Get")), typeof(IService), "Get", "Get");
        var call = method.Capture();
        Gen.Select(Gen.Int, Gen.String[Gen.Char["a:\\\0\ud800\udfffé"], 0, 40], Gen.Int[0, 256]).Sample((number, text, capacity) =>
        {
            var key = new KeyBuilder(call, new char[capacity]);
            var date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var offset = new DateTimeOffset(date);
            var guid = new Guid(number, 0, 0, new byte[8]);
            key.AppendPart(text);
            key.AppendPart(number);
            key.AppendPart((long)number);
            key.AppendPart((uint)number);
            key.AppendPart((ulong)(uint)number);
            key.AppendPart((byte)number);
            key.AppendPart((sbyte)number);
            key.AppendPart((short)number);
            key.AppendPart((ushort)number);
            key.AppendPart((float)number);
            key.AppendPart((double)number);
            key.AppendPart((decimal)number);
            key.AppendPart(number % 2 == 0);
            key.AppendPart(':');
            key.AppendPart(guid);
            key.AppendPart(date);
            key.AppendPart(offset);
            key.AppendPart(Number.Min);
            key.AppendNullPart();
            Assert.Equal(MethodCacheKeys.For(typeof(IService), "Get",
                MethodCacheKeys.Part(text), MethodCacheKeys.Part(number), MethodCacheKeys.Part((long)number),
                MethodCacheKeys.Part((uint)number), MethodCacheKeys.Part((ulong)(uint)number), MethodCacheKeys.Part((byte)number),
                MethodCacheKeys.Part((sbyte)number), MethodCacheKeys.Part((short)number), MethodCacheKeys.Part((ushort)number),
                MethodCacheKeys.Part((float)number), MethodCacheKeys.Part((double)number), MethodCacheKeys.Part((decimal)number),
                MethodCacheKeys.Part(number % 2 == 0), MethodCacheKeys.Part(':'), MethodCacheKeys.Part(guid),
                MethodCacheKeys.Part(date), MethodCacheKeys.Part(offset), MethodCacheKeys.Part(Number.Min), MethodCacheKeys.Null), key.ToString());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }
}
