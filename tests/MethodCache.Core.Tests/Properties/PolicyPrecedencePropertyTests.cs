using System.Runtime.CompilerServices;
using CsCheck;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.Tests.Core;
using MethodCache.Policies;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// The precedence rules of policy resolution (docs/features/configuration.md), for random combinations of layers.
/// </summary>
/// <remarks>
/// Four layers (attributes 10, startup code 40, configuration 50, runtime overrides 100) may each configure the
/// method itself, its group and all methods, each setting a random subset of fields. For every field, the documented
/// winner is: the method's own setting from the highest layer that sets it; failing that, the group's from the
/// highest layer; failing that, the defaults' from the highest layer; else unset. Tags instead combine across all
/// of them (see <see cref="ExpectedTags"/>). Seeds and replay: see
/// <see cref="PropertySettings"/>.
/// </remarks>
[Collection(nameof(CpuIntensiveCollection))]
public class PolicyPrecedencePropertyTests
{
    private const string MethodId = "MethodCache.Core.Tests.Properties.IPrecedenceService.Get";
    private const string Group = "g";

    private static readonly (string SourceId, int Priority)[] Layers =
    {
        (PolicySourceIds.Attributes, PolicySourcePriority.Attributes),
        (PolicySourceIds.StartupFluent, PolicySourcePriority.StartupFluent),
        (PolicySourceIds.ConfigurationFiles, PolicySourcePriority.ConfigurationFiles),
        (PolicySourceIds.RuntimeOverrides, PolicySourcePriority.RuntimeOverrides),
    };

    /// <summary>Scopes from the most to the least specific.</summary>
    private enum Scope { Method, Group, AllMethods }

    private static readonly CachePolicyFields[] Fields =
    {
        CachePolicyFields.Duration, CachePolicyFields.Version, CachePolicyFields.SlidingExpiration,
        CachePolicyFields.FailSafe, CachePolicyFields.Tags, CachePolicyFields.ClearTags,
    };

    /// <summary>For each layer and scope, which of <see cref="Fields"/> it sets (a bit mask), and whether the method is in the group.</summary>
    private static readonly Gen<(int[] Masks, bool InGroup)> GenSetup =
        Gen.Select(Gen.Int[0, (1 << Fields.Length) - 1].Array[Layers.Length * 3], Gen.Bool);

    [Fact]
    public async Task ResolvedPolicy_TakesEachFieldFromTheMostSpecificScope_ThenTheHighestLayer()
    {
        await GenSetup.SampleAsync(async (masks, inGroup) =>
        {
            var sources = Layers.Select((layer, l) => new Registration(layer.SourceId, layer.Priority, Snapshots(l, masks, inGroup))).ToArray();
            var resolver = new PolicyResolver(sources.Select(s => new PolicySourceRegistration(s.Source, s.Priority)));
            try
            {
                var policy = (await resolver.ResolveAsync(MethodId)).Policy;

                Assert.Equal(Expected(CachePolicyFields.Duration, masks, inGroup) is { } d ? TimeSpan.FromMinutes(d) : null, policy.Duration);
                Assert.Equal(Expected(CachePolicyFields.Version, masks, inGroup), policy.Version);
                Assert.Equal(Expected(CachePolicyFields.SlidingExpiration, masks, inGroup) is { } s ? TimeSpan.FromSeconds(s) : null, policy.SlidingExpiration);
                Assert.Equal(Expected(CachePolicyFields.FailSafe, masks, inGroup) is { } f ? TimeSpan.FromHours(f) : null, policy.FailSafe);
                Assert.Equal(ExpectedTags(masks, inGroup), policy.Tags);
            }
            finally
            {
                await resolver.DisposeAsync();
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations / 4, print: Print);
    }

    /// <summary>The value each layer and scope writes: unique, so the test can tell which one won.</summary>
    private static int ValueOf(int layer, Scope scope) => 1 + (layer * 3) + (int)scope;

    private static int? Expected(CachePolicyFields field, int[] masks, bool inGroup)
    {
        var bit = 1 << Array.IndexOf(Fields, field);
        foreach (var scope in new[] { Scope.Method, Scope.Group, Scope.AllMethods })
        {
            if (scope == Scope.Group && !inGroup)
            {
                continue;
            }

            for (var layer = Layers.Length - 1; layer >= 0; layer--)
            {
                if ((Mask(masks, layer, scope) & bit) != 0)
                {
                    return ValueOf(layer, scope);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tags combine: every layer and scope that sets them adds its tag, from the lowest to the highest, until one
    /// that clears them (<see cref="CachePolicyFields.ClearTags"/>), whose tags replace those below it.
    /// </summary>
    private static string[] ExpectedTags(int[] masks, bool inGroup)
    {
        var tagsBit = 1 << Array.IndexOf(Fields, CachePolicyFields.Tags);
        var clearBit = 1 << Array.IndexOf(Fields, CachePolicyFields.ClearTags);
        var highestFirst = new List<string>();
        foreach (var scope in new[] { Scope.Method, Scope.Group, Scope.AllMethods })
        {
            if (scope == Scope.Group && !inGroup)
            {
                continue;
            }

            for (var layer = Layers.Length - 1; layer >= 0; layer--)
            {
                var mask = Mask(masks, layer, scope);
                if ((mask & tagsBit) != 0)
                {
                    highestFirst.Add("tag" + ValueOf(layer, scope));
                }

                if ((mask & clearBit) != 0)
                {
                    highestFirst.Reverse();
                    return highestFirst.ToArray();
                }
            }
        }

        highestFirst.Reverse();
        return highestFirst.ToArray();
    }

    private static int Mask(int[] masks, int layer, Scope scope) => masks[(layer * 3) + (int)scope];

    private static List<PolicySnapshot> Snapshots(int layer, int[] masks, bool inGroup)
    {
        var snapshots = new List<PolicySnapshot>();
        foreach (var scope in Enum.GetValues<Scope>())
        {
            var mask = Mask(masks, layer, scope);

            // Every cached method is listed by the attribute layer; the group comes from its [Cache(Group = "g")]
            var listsMethod = scope == Scope.Method && layer == 0;
            if (mask == 0 && !listsMethod)
            {
                continue;
            }

            var value = ValueOf(layer, scope);
            var fields = Fields.Where((_, i) => (mask & (1 << i)) != 0).Aggregate(CachePolicyFields.None, (a, f) => a | f);
            var metadata = new Dictionary<string, string?>();
            if (listsMethod && inGroup)
            {
                metadata["group"] = Group;
                fields |= CachePolicyFields.Metadata;
            }

            var policy = CachePolicy.Empty with
            {
                Duration = fields.HasFlag(CachePolicyFields.Duration) ? TimeSpan.FromMinutes(value) : null,
                Version = fields.HasFlag(CachePolicyFields.Version) ? value : null,
                SlidingExpiration = fields.HasFlag(CachePolicyFields.SlidingExpiration) ? TimeSpan.FromSeconds(value) : null,
                FailSafe = fields.HasFlag(CachePolicyFields.FailSafe) ? TimeSpan.FromHours(value) : null,
                Tags = fields.HasFlag(CachePolicyFields.Tags) ? new[] { "tag" + value } : Array.Empty<string>(),
                Metadata = metadata,
            };

            var id = scope switch
            {
                Scope.Method => MethodId,
                Scope.Group => MethodIds.Group(Group),
                _ => MethodIds.AllMethods,
            };
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(Layers[layer].SourceId, id, policy, fields, DateTimeOffset.UtcNow, metadata));
        }

        return snapshots;
    }

    private static string Print((int[] Masks, bool InGroup) setup)
    {
        var lines = new List<string> { setup.InGroup ? "method in group g" : "method in no group" };
        for (var layer = 0; layer < Layers.Length; layer++)
        {
            foreach (var scope in Enum.GetValues<Scope>())
            {
                var mask = Mask(setup.Masks, layer, scope);
                if (mask != 0)
                {
                    lines.Add($"{Layers[layer].SourceId} {scope}: {string.Join(", ", Fields.Where((_, i) => (mask & (1 << i)) != 0))} = {ValueOf(layer, scope)}");
                }
            }
        }

        return string.Join("\n  ", lines);
    }

    private sealed record Registration(string SourceId, int Priority, IReadOnlyCollection<PolicySnapshot> Snapshots)
    {
        public IPolicySource Source { get; } = new StaticSource(SourceId, Snapshots);
    }

    private sealed class StaticSource(string sourceId, IReadOnlyCollection<PolicySnapshot> snapshots) : IPolicySource
    {
        public string SourceId { get; } = sourceId;

        public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshots);

        public async IAsyncEnumerable<PolicyChange> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }
}
