using CsCheck;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class PolicyIdentityProperties
{
    [Fact]
    public void Fingerprint_TagOrderDuplicatesAndAuditAreIrrelevant_AllBehaviorFieldsAreSignificant()
    {
        Gen.Select(Gen.Int[1, 10000], Gen.String[Gen.Char["a:\\0éİ\ud800\udfff"], 0, 30]).Sample((seconds, text) =>
        {
            var policy = CacheRuntimePolicy.Empty("M") with
            {
                Duration = TimeSpan.FromSeconds(seconds),
                Tags = [text, "second"],
                Metadata = new Dictionary<string, string?> { ["group"] = text }
            };
            var hash = PolicyIdentity.Shared(policy);
            Assert.Equal(hash, PolicyIdentity.Shared(policy with
            {
                Tags = ["second", text, text],
                Metadata = new Dictionary<string, string?> { ["group"] = text, ["owner"] = "audit" }
            }));
            foreach (var changed in new[]
            {
                policy with { Duration = policy.Duration!.Value + TimeSpan.FromTicks(1) },
                policy with { SlidingExpiration = TimeSpan.FromSeconds(1) },
                policy with { RefreshAhead = TimeSpan.FromSeconds(1) },
                policy with { FailSafe = TimeSpan.FromSeconds(1) },
                policy with { FailSafeTimeout = TimeSpan.FromSeconds(1) },
                policy with { FailSafeRetryAfter = TimeSpan.FromSeconds(1) },
                policy with { Version = 1 }, policy with { Enabled = false }, policy with { Distributed = false },
                policy with { Metadata = new Dictionary<string, string?> { ["group"] = text + "x" } },
                policy with { Tags = [text, "third"] }
            }) Assert.NotEqual(hash, PolicyIdentity.Shared(changed));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void Fingerprint_UnsetEffectiveDefaults_AreEquivalentToExplicitDefaults()
    {
        var empty = CacheRuntimePolicy.Empty("M");
        Assert.Equal(PolicyIdentity.Shared(empty), PolicyIdentity.Shared(empty with
        {
            Duration = TimeSpan.FromMinutes(5),
            Enabled = true,
            Distributed = true,
            FailSafe = TimeSpan.Zero,
            FailSafeRetryAfter = TimeSpan.FromSeconds(30)
        }));
        Assert.NotEqual(PolicyIdentity.Private(), PolicyIdentity.Private());
    }
}
