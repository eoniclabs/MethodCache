using CsCheck;
using MethodCache.Core.Tests.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using NSubstitute;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Key and tag injection, from the threat list in docs/security.md: an argument a user controls (a user name, a
/// search term) may contain the characters keys and tags are built from. Such an argument must never produce
/// another call's key, since that call's cached result would be returned to this caller. Tags are plain text, so a
/// crafted argument can produce another template's tag; that can only remove more entries than intended, which the
/// last tests pin and the docs state. Seeds and replay: see <see cref="PropertySettings"/>.
/// </summary>
[Collection(nameof(CpuIntensiveCollection))]
public class KeyInjectionPropertyTests
{
    /// <summary>Separators and look-alikes from every key and tag format: inline keys, records, tags, wildcards, the backplane.</summary>
    private static readonly Gen<string> GenHostile = Gen.Frequency(
        (4, Gen.String[Gen.Char[":(){}\\\0\n\r*?|,=;\"' a1"], 0, 5]),
        (1, Gen.OneOfConst(":", "\\:", "\\0", "\0", "(", ")", "():", "{userId}", "1:orders", "*", "\n", "null", "")));

    private static readonly Gen<string?> GenNullableHostile = GenHostile.Null(0.15);

    public sealed record Pair(string? A, string? B);

    private interface IUsers { }

    [Fact]
    public async Task FluentKeys_VersionSuffixCannotAliasAnUnversionedUserKey()
    {
        var manager = CapturingManager();
        var versioned = await StoredKey(manager, "entry", 1);
        var unversioned = await StoredKey(manager, "entry::v1", null);
        Assert.NotEqual(versioned, unversioned);
    }

    [Fact]
    public async Task FluentKeys_HostileKeysAndVersionsHaveUnambiguousIdentities()
    {
        var manager = CapturingManager();
        var versions = Gen.Int[0, 10].Nullable(0.3);
        await Gen.Select(GenHostile, versions, GenHostile, versions).SampleAsync(async (a, av, b, bv) =>
        {
            var ak = "key:" + a;
            var bk = "key:" + b;
            var left = await StoredKey(manager, ak, av);
            var right = await StoredKey(manager, bk, bv);
            Assert.Equal(left, CacheManagerExtensions.GetCacheKey(ak, av));
            Assert.Equal(right, CacheManagerExtensions.GetCacheKey(bk, bv));
            Assert.Equal(ak == bk && av == bv, left == right);
            if (av.HasValue) Assert.NotEqual(left, await StoredKey(manager, left, null));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public async Task FluentKeys_LeadingControlCharactersAndVersions_CanBeInvalidatedUsingTheirPublicIdentity()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        services.AddMethodCache();
        services.Configure<MethodCacheMemoryOptions>(o => { o.EnableBackgroundCleanup = false; o.MaxItems = 16; });
        var versions = Gen.Int[0, 10].Nullable(0.5);
        await Gen.Select(GenHostile, versions).SampleAsync(async (text, version) =>
        {
            // Every sample has its own cache: unrelated samples must not evict the entry under test.
            await using var provider = services.BuildServiceProvider();
            var cache = provider.GetRequiredService<ICacheManager>();
            var loads = 0;
            var key = "\0" + text;
            var identity = CacheManagerExtensions.GetCacheKey(key, version);
            ValueTask<int> Load() => cache.GetOrCreateAsync(key, (_, _) => ValueTask.FromResult(++loads),
                options => { if (version is { } value) options.WithVersion(value); });
            await cache.InvalidateByKeysAsync(identity);
            var first = await Load();
            Assert.Equal(first, await Load());
            await cache.InvalidateByKeysAsync(identity);
            Assert.NotEqual(first, await Load());
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    private static ICacheManager CapturingManager()
    {
        var manager = Substitute.For<ICacheManager>();
        manager.GetOrCreateAsync(Arg.Any<string>(), Arg.Any<Func<CacheContext, CancellationToken, ValueTask<string>>>(),
            Arg.Any<CacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(call.ArgAt<CacheEntryOptions>(2).GetCacheKey(call.ArgAt<string>(0))));
        return manager;
    }

    private static ValueTask<string> StoredKey(ICacheManager manager, string key, int? version) =>
        manager.GetOrCreateAsync(key, (_, _) => ValueTask.FromResult("unused"),
            b => { if (version is { } v) b.WithVersion(v); });

    [Fact]
    public void InlineKeys_CraftedStrings_NeverShareAKey()
    {
        // GetOrders(string userId, string? filter): equal keys exactly when both arguments are equal
        Gen.Select(GenNullableHostile, GenNullableHostile, GenNullableHostile, GenNullableHostile).Sample((a1, b1, a2, b2) =>
        {
            var k1 = MethodCacheKeys.For(typeof(IUsers), "GetOrders", MethodCacheKeys.Part(a1), MethodCacheKeys.Part(b1));
            var k2 = MethodCacheKeys.For(typeof(IUsers), "GetOrders", MethodCacheKeys.Part(a2), MethodCacheKeys.Part(b2));

            Assert.Equal(a1 == a2 && b1 == b2, k1 == k2);
            Assert.Equal(k1, Built(a1, b1));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations, print: t => Describe(t.Item1, t.Item2, t.Item3, t.Item4));
    }

    [Fact]
    public void InlineRecordKeys_CraftedMembers_NeverShareAKey()
    {
        // GetOrders(Pair? query, string? page), written as the generated decorator writes a record: (A:B), or \0
        var genPair = Gen.Select(GenNullableHostile, GenNullableHostile, (a, b) => new Pair(a, b)).Null(0.15);
        Gen.Select(genPair, GenNullableHostile, genPair, GenNullableHostile).Sample((p1, s1, p2, s2) =>
        {
            Assert.Equal(Equals(p1, p2) && s1 == s2, RecordKey(p1, s1) == RecordKey(p2, s2));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void HashedKeys_CraftedStrings_NeverShareAKey()
    {
        var generator = new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Users.GetOrders");
        Gen.Select(GenHostile.Array[1, 3], GenHostile.Array[1, 3]).Sample((args1, args2) =>
        {
            var k1 = generator.GenerateKey("Users.GetOrders", args1.Cast<object?>().ToArray(), policy);
            var k2 = generator.GenerateKey("Users.GetOrders", args2.Cast<object?>().ToArray(), policy);

            Assert.Equal(args1.SequenceEqual(args2), k1 == k2);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void ACraftedArgument_CannotReachAnotherMethodsKeys()
    {
        // GetOrders(string) and GetOrdersSummary(): no argument of the first gives the key of the second, and a
        // version is a fixed part of the prefix that no argument can supply
        var summary = MethodCacheKeys.For(typeof(IUsers), "GetOrdersSummary");
        var versioned = MethodCacheKeys.ForVersion(typeof(IUsers), "GetOrders", 2, MethodCacheKeys.Part("1"));
        GenNullableHostile.Sample(arg =>
        {
            var key = MethodCacheKeys.For(typeof(IUsers), "GetOrders", MethodCacheKeys.Part(arg));
            Assert.NotEqual(summary, key);
            Assert.NotEqual(versioned, key);
            Assert.StartsWith(MethodCacheKeys.Prefix(typeof(IUsers), "GetOrders") + ":", key);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void TagFromOneArgument_IdentifiesThatArgument()
    {
        // One placeholder per tag: different user ids never give the same tag, whatever text they contain
        var policy = CacheRuntimePolicy.Empty("Users.GetOrders") with { Tags = new[] { "user:{userId}:orders" } };
        Gen.Select(GenHostile, GenHostile).Sample((u1, u2) =>
        {
            var t1 = CacheTagTemplates.Expand(policy, new[] { "userId" }, new[] { u1 }).Tags.Single();
            var t2 = CacheTagTemplates.Expand(policy, new[] { "userId" }, new[] { u2 }).Tags.Single();

            Assert.Equal(u1 == u2, t1 == t2);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void TagTemplates_WithNumericIds_NeverCollideAcrossTemplates()
    {
        // The safe pattern the docs recommend: ids that can't contain the separator
        var profile = CacheRuntimePolicy.Empty("Users.Get") with { Tags = new[] { "user:{userId}" } };
        var orders = CacheRuntimePolicy.Empty("Orders.Get") with { Tags = new[] { "user:{userId}:orders" } };
        Gen.Select(Gen.Long, Gen.Long).Sample((a, b) =>
        {
            var t1 = CacheTagTemplates.Expand(profile, new[] { "userId" }, new[] { a.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Tags.Single();
            var t2 = CacheTagTemplates.Expand(orders, new[] { "userId" }, new[] { b.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Tags.Single();

            Assert.NotEqual(t1, t2);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void TagTemplates_WithFreeTextArguments_CanCollideAcrossTemplates()
    {
        // Not guaranteed, and stated in docs/security.md: tags are plain text. "user:{userId}" for the id "1:orders"
        // is the tag "user:{userId}:orders" gives user 1, so invalidating one also removes the other.
        var profile = CacheRuntimePolicy.Empty("Users.Get") with { Tags = new[] { "user:{userId}" } };
        var orders = CacheRuntimePolicy.Empty("Orders.Get") with { Tags = new[] { "user:{userId}:orders" } };

        var crafted = CacheTagTemplates.Expand(profile, new[] { "userId" }, new[] { "1:orders" }).Tags.Single();
        var victim = CacheTagTemplates.Expand(orders, new[] { "userId" }, new[] { "1" }).Tags.Single();

        Assert.Equal(victim, crafted);
    }

    [Fact]
    public async Task ACollidingTag_OnlyRemovesEntries_NeverServesThemToSomeoneElse()
    {
        // What the collision above can do: user 1's orders are loaded again. Their values stay under their own keys.
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var orders = CacheRuntimePolicy.Empty("Orders.Get") with { Duration = TimeSpan.FromMinutes(1), Tags = new[] { "user:1:orders" } };
        var profile = CacheRuntimePolicy.Empty("Users.Get") with { Duration = TimeSpan.FromMinutes(1), Tags = new[] { "user:1:orders" } };

        await cache.GetOrCreateFastAsync("Orders.Get:1", "Orders.Get", () => Task.FromResult("user 1's orders"), orders);
        var attacker = await cache.GetOrCreateFastAsync("Users.Get:1\\:orders", "Users.Get", () => Task.FromResult("attacker's profile"), profile);
        await cache.InvalidateByTagsAsync("user:1:orders");
        var reloaded = await cache.GetOrCreateFastAsync("Orders.Get:1", "Orders.Get", () => Task.FromResult("user 1's orders, reloaded"), orders);

        Assert.Equal("attacker's profile", attacker);
        Assert.Equal("user 1's orders, reloaded", reloaded);
    }

    [Theory]
    [InlineData("user:*")]
    [InlineData("*")]
    [InlineData("user:?")]
    public async Task InvalidateByTags_MatchesWildcardsLiterally(string tag)
    {
        // Only InvalidateByTagPatternAsync reads * and ?; never build a pattern from untrusted text
        var services = new ServiceCollection();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var policy = CacheRuntimePolicy.Empty("Users.Get") with { Duration = TimeSpan.FromMinutes(1), Tags = new[] { "user:1" } };
        await cache.GetOrCreateFastAsync("Users.Get:1", "Users.Get", () => Task.FromResult("Ada"), policy);

        await cache.InvalidateByTagsAsync(tag);
        await cache.InvalidateByKeysAsync(tag);

        Assert.True(cache.TryGetFast<string>("Users.Get:1", out var value));
        Assert.Equal("Ada", value);
    }

    private static string Built(string? a, string? b)
    {
        var builder = new MethodCacheKeyBuilder(stackalloc char[4]);
        builder.Append(MethodCacheKeys.Prefix(typeof(IUsers), "GetOrders"));
        builder.AppendPart(a);
        builder.AppendPart(b);
        return builder.ToString();
    }

    /// <summary>The expression the source generator emits for a record argument made of simple members (AddObjectPieces).</summary>
    private static string RecordKey(Pair? pair, string? page)
        => string.Concat(
            MethodCacheKeys.Prefix(typeof(IUsers), "GetOrders"),
            MethodCacheKeys.Separator,
            pair is null ? MethodCacheKeys.Null : string.Concat("(", MethodCacheKeys.Part(pair.A), MethodCacheKeys.Separator, MethodCacheKeys.Part(pair.B), ")"),
            MethodCacheKeys.Separator,
            MethodCacheKeys.Part(page));

    private static string Describe(params string?[] values)
        => string.Join(", ", values.Select(v => v is null ? "null" : '"' + v.Replace("\0", "\\0").Replace("\n", "\\n") + '"'));
}
