using System.Security.Cryptography;
using MethodCache.Policies;

namespace MethodCache.Infrastructure;

internal static class PolicyIdentity
{
    internal static string Private() => "p1:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal static string Shared(CacheRuntimePolicy policy)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1); // Canonical policy representation version, independent of entry/wire versions.
        Text(writer, policy.MethodId);
        writer.Write((policy.Duration ?? TimeSpan.FromMinutes(5)).Ticks);
        writer.Write((policy.SlidingExpiration ?? TimeSpan.Zero).Ticks);
        writer.Write((policy.RefreshAhead ?? TimeSpan.Zero).Ticks);
        writer.Write((policy.FailSafe ?? TimeSpan.Zero).Ticks);
        writer.Write((policy.FailSafeTimeout ?? TimeSpan.Zero).Ticks);
        writer.Write((policy.FailSafeRetryAfter ?? TimeSpan.FromSeconds(30)).Ticks);
        writer.Write(policy.Version ?? -1);
        writer.Write(policy.Enabled != false);
        writer.Write(policy.Distributed != false);
        Text(writer, policy.Metadata.GetValueOrDefault("group") ?? "");
        writer.Write(policy.CanonicalTagParts);
        var tags = policy.Tags.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        writer.Write(tags.Length);
        foreach (var tag in tags) Text(writer, tag);
        writer.Flush();
        return "s1:" + Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static bool SameBehavior(CacheRuntimePolicy left, CacheRuntimePolicy right)
        => Shared(left) == Shared(right) && SamePredicate(left.CacheWhen, right.CacheWhen);

    private static bool SamePredicate(Func<object?, bool>? left, Func<object?, bool>? right)
        => ReferenceEquals(left, right) || left?.Target is ICachePredicate a && right?.Target is ICachePredicate b && ReferenceEquals(a.Identity, b.Identity);

    internal static bool IsRuntime(PolicyResolutionResult result)
        => result.Contributions.Any(static contribution => contribution.SourceId == "runtime-overrides" &&
            (contribution.Fields & ~CachePolicyFields.Metadata) != CachePolicyFields.None) &&
            (result.Declaration == null || !SameBehavior(CacheRuntimePolicy.FromResolverResult(result),
                CacheRuntimePolicy.FromPolicy(result.MethodId, result.Declaration, CachePolicyFields.None)));

    private static void Text(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        foreach (var codeUnit in value) writer.Write((ushort)codeUnit);
    }
}
