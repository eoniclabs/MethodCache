using CsCheck;
using MethodCache.Core.Runtime.Keys;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Extensibility;
using MethodCache.Generated;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class PrefixCollisionTests
{
    [Fact]
    public void ForcedCollisions_AllLookupAndMutationPaths_MatchDictionaryModel()
    {
        using var manager = new InMemoryCacheManager();
        var services = new DecoratorServices(manager, Substitute.For<IPolicyRegistry>(), new FastHashKeyGenerator());
        var call = new MethodCache.Generated.CachedMethod(services, PolicyHandle.Fixed(CacheRuntimePolicy.Empty("M")), typeof(IService), "M", "M").Capture();
        Gen.Select(Gen.Int[0, 5], Gen.Int[0, 15], Gen.Int).Array[1, 100].Sample(operations =>
        {
            var engine = Engine(1000, new KeyComparer(0));
            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            Span<char> buffer = stackalloc char[128];
            foreach (var (operation, id, value) in operations)
            {
                var builder = call.StartKey(buffer);
                builder.AppendPart(id);
                var key = builder.ToString();
                switch (operation)
                {
                    case 0: engine.Add(Node(key, value)); expected[key] = value; break;
                    case 1:
                        Assert.Equal(expected.Remove(key), engine.Remove(new string(key.AsSpan())));
                        break;
                    case 2:
                        if (engine.TryPeek(key, out var current))
                        {
                            Assert.True(engine.TryReplace(current, Node(key, value)));
                            expected[key] = value;
                        }
                        break;
                    case 3: Check(expected, key, engine.TryGet(new string(key.AsSpan()), 0, out var text), text); break;
                    case 4: Check(expected, key, engine.TryGet(key.AsSpan(), 0, out var span), span); break;
                    case 5: Check(expected, key, engine.TryGet(ref builder, 0, out var built), built); break;
                }
            }
            Assert.Equal(expected.Count, engine.Count);
        }, seed: PropertySettings.Seed, iter: PropertySettings.ModelIterations);
    }

    [Fact]
    public async Task ForcedCollisions_ConcurrentPublicationsNeverReturnAnotherKeysValue()
    {
        var engine = Engine(32, new KeyComparer(0));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var key = "shared-prefix:" + worker + ":" + i % 64;
                engine.Add(Node(key, worker));
                if (engine.TryGet(key.AsSpan(), 0, out var node))
                {
                    Assert.Equal(key, node.Key);
                    Assert.Equal(worker, Assert.IsType<ValueNode<int>>(node).Value);
                }
                if ((i & 7) == 0) engine.Remove(key);
            }
        })));
        engine.Maintain();
        Assert.InRange(engine.Count, 0, 32);
    }

    [Fact]
    public void ForcedDictionaryCollisions_PreserveFrequencyAdmissionAndEviction()
    {
        var normal = Engine(16, KeyComparer.Instance);
        var collisions = Engine(16, new KeyComparer(0));
        for (var i = 0; i < 16; i++) { normal.Add(Node("k" + i, i)); collisions.Add(Node("k" + i, i)); }
        for (var i = 0; i < 1000; i++)
        {
            var key = "k" + i % 4;
            Assert.Equal(normal.TryGet(key, 0, out _), collisions.TryGet(key.AsSpan(), 0, out _));
        }
        for (var i = 16; i < 100; i++) { normal.Add(Node("k" + i, i)); collisions.Add(Node("k" + i, i)); }
        normal.Maintain(); collisions.Maintain();
        Assert.Equal(normal.Map.Keys.Order(StringComparer.Ordinal), collisions.Map.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(normal.EvictionCount, collisions.EvictionCount);
    }

    private static MemoryCacheEngine Engine(int size, KeyComparer comparer)
        => new(size, EngineEvictionMode.TinyLfu, () => 0, size, comparer) { ForegroundMaintenance = true };
    private static ValueNode<int> Node(string key, int value)
        => new(key, value) { LogicalExpiresAt = long.MaxValue, PhysicalExpiresAt = long.MaxValue, AbsoluteExpiresAt = long.MaxValue };
    private static void Check(Dictionary<string, int> expected, string key, bool found, Node? node)
    {
        Assert.Equal(expected.TryGetValue(key, out var value), found);
        if (found) { Assert.Equal(key, node!.Key); Assert.Equal(value, Assert.IsType<ValueNode<int>>(node).Value); }
    }
    private interface IService { }
}
