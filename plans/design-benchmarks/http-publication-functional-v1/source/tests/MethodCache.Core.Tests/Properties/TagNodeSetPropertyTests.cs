using CsCheck;
using MethodCache.Core.Runtime.Memory;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class TagNodeSetPropertyTests
{
    [Fact]
    public void AddRemoveAndPromotion_MatchReferenceIdentitySet()
    {
        Gen.Select(Gen.Int[0, 15], Gen.Bool).Array[1, 100].Sample(operations =>
        {
            // Equal key/value text still denotes different node versions during replacement.
            var nodes = Enumerable.Range(0, 16).Select(_ => new ValueNode<int>("same-key", 1)).ToArray();
            var actual = new TagNodeSet();
            var expected = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            foreach (var (index, add) in operations)
            {
                var node = nodes[index];
                Assert.Equal(add ? expected.Add(node) : expected.Remove(node), add ? actual.Add(node) : actual.Remove(node));
                Assert.Equal(expected.Count == 0, actual.IsEmpty);
                Assert.True(expected.SetEquals(actual.Keys));
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    public async Task ConcurrentPromotionAndRemoval_PreserveEverySurvivingNode(int count)
    {
        for (var round = 0; round < 30; round++)
        {
            var set = new TagNodeSet();
            var nodes = Enumerable.Range(0, count).Select(_ => new ValueNode<int>("same-key", 1)).ToArray();
            using var start = new ManualResetEventSlim();
            var writers = nodes.Select((node, index) => Task.Run(() =>
            {
                start.Wait();
                Assert.True(set.Add(node));
                Assert.False(set.Add(node));
                if (index % 2 == 0) Assert.True(set.Remove(node));
            })).ToArray();
            start.Set();
            await Task.WhenAll(writers);
            var expected = nodes.Where((_, index) => index % 2 != 0).ToHashSet<Node>(ReferenceEqualityComparer.Instance);
            Assert.True(expected.SetEquals(set.Keys));
            Assert.Equal(expected.Count == 0, set.IsEmpty);
            foreach (var node in expected) Assert.True(set.Remove(node));
            Assert.True(set.IsEmpty);
            Assert.Empty(set.Keys);
        }
    }
}
