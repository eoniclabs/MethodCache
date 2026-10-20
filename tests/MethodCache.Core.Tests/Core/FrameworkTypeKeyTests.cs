using System.Net;
using System.Numerics;
using System.Text.Json;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Framework types that are common as arguments have a key form in every key generator: two different values never
/// share a key, and equal values always do, also inside an argument such as a record with a list. Their public
/// properties don't hold their value (BigInteger has IsZero and Sign, IPAddress throws from ScopeId), so writing
/// those would merge different values or fail.
/// </summary>
public class FrameworkTypeKeyTests
{
    public sealed record Holder(object? Value, List<string> Tags);

    private static readonly CacheRuntimePolicy Policy = CacheRuntimePolicy.Empty("Framework");

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var generator in new[] { nameof(FastHashKeyGenerator), nameof(JsonKeyGenerator) })
        {
            foreach (var name in Values.Keys)
            {
                data.Add(generator, name);
            }
        }

        return data;
    }

    /// <summary>Per type: two different values, and a copy of the first one made separately.</summary>
    private static readonly Dictionary<string, Func<(object A, object B, object ACopy)>> Values = new()
    {
        ["BigInteger"] = () => (new BigInteger(3), new BigInteger(5), BigInteger.Parse("3")),
        ["large BigInteger"] = () => (BigInteger.Pow(2, 100), BigInteger.Pow(2, 100) + 1, BigInteger.Pow(2, 100)),
        ["Complex"] = () => (new Complex(1, 2), new Complex(2, 1), new Complex(1, 2)),
        ["IPv4 address"] = () => (IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.0.0.1")),
        ["IPAddress constant"] = () => (IPAddress.Loopback, IPAddress.Any, IPAddress.Parse("127.0.0.1")),
        ["IPv6 address with scope"] = () => (IPAddress.Parse("fe80::1%1"), IPAddress.Parse("fe80::1%2"), IPAddress.Parse("fe80::1%1")),
        ["JsonElement"] = () => (Json("{\"a\":1}"), Json("{\"a\":2}"), Json("{\"a\":1}")),
        ["Half"] = () => ((Half)1.5, (Half)2.5, (Half)1.5),
        ["Int128"] = () => (Int128.MaxValue, Int128.MaxValue - 1, Int128.MaxValue),
        ["UInt128"] = () => (UInt128.MaxValue, UInt128.MaxValue - 1, UInt128.MaxValue),
        ["DateOnly"] = () => (new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)),
        ["TimeOnly"] = () => (new TimeOnly(12, 0, 0, 1), new TimeOnly(12, 0, 0, 2), new TimeOnly(12, 0, 0, 1)),
        ["Uri"] = () => (new Uri("https://example.com/a"), new Uri("https://example.com/b"), new Uri("https://example.com/a")),
        ["Version"] = () => (new Version(1, 2, 3), new Version(1, 2, 4), new Version(1, 2, 3)),
        ["TimeSpan"] = () => (TimeSpan.FromTicks(1), TimeSpan.FromTicks(2), TimeSpan.FromTicks(1)),
        ["Index"] = () => (new Index(1), new Index(1, fromEnd: true), new Index(1)),
        ["Range"] = () => (1..3, 1..^3, 1..3),
        ["Vector2"] = () => (new Vector2(1, 2), new Vector2(2, 1), new Vector2(1, 2)),
        ["Vector3"] = () => (new Vector3(1, 2, 3), new Vector3(1, 2, 4), new Vector3(1, 2, 3)),
        ["Vector4"] = () => (new Vector4(1, 2, 3, 4), new Vector4(1, 2, 3, 5), new Vector4(1, 2, 3, 4)),
        ["Quaternion"] = () => (new Quaternion(1, 2, 3, 4), new Quaternion(1, 2, 3, 5), new Quaternion(1, 2, 3, 4)),
        ["Plane"] = () => (new Plane(1, 2, 3, 4), new Plane(1, 2, 3, 5), new Plane(1, 2, 3, 4)),
        ["Matrix3x2"] = () => (Matrix3x2.CreateRotation(1), Matrix3x2.CreateRotation(2), Matrix3x2.CreateRotation(1)),
        ["Matrix4x4"] = () => (Matrix4x4.CreateScale(2), Matrix4x4.CreateScale(3), Matrix4x4.CreateScale(2)),
        ["ReadOnlyMemory"] = () => (new ReadOnlyMemory<int>([1, 2]), new ReadOnlyMemory<int>([1, 3]), new ReadOnlyMemory<int>([1, 2])),
        ["Memory of bytes"] = () => (new Memory<byte>([1, 2]), new Memory<byte>([1, 3]), new Memory<byte>([1, 2])),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FrameworkValue_DifferentValuesGetDifferentKeys_AndEqualValuesTheSameKey(string generatorName, string type)
    {
        var generator = Create(generatorName);
        var (a, b, aCopy) = Values[type]();

        Assert.NotEqual(Key(generator, a), Key(generator, b));
        Assert.Equal(Key(generator, a), Key(generator, aCopy));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FrameworkValue_InsideAnArgument_DifferentValuesGetDifferentKeys(string generatorName, string type)
    {
        var generator = Create(generatorName);
        var (a, b, aCopy) = Values[type]();

        Assert.NotEqual(Key(generator, new Holder(a, [])), Key(generator, new Holder(b, [])));
        Assert.Equal(Key(generator, new Holder(a, [])), Key(generator, new Holder(aCopy, [])));
        Assert.NotEqual(Key(generator, new List<object> { a }), Key(generator, new List<object> { b }));
    }

    private static string Key(ICacheKeyGenerator generator, object value) => generator.GenerateKey("Framework", [value], Policy);

    private static ICacheKeyGenerator Create(string name) => name switch
    {
        nameof(FastHashKeyGenerator) => new FastHashKeyGenerator(),
        _ => new JsonKeyGenerator(),
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
