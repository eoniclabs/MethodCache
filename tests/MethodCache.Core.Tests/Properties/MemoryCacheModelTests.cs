using System.Text;
using System.Text.RegularExpressions;
using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Model-based test of <see cref="InMemoryCacheManager"/>: random sequences of loads, reads, writes, failing loads,
/// key, tag and pattern invalidations and clock advances run against the real cache and against a plain dictionary
/// with the documented expiry rules. Every operation records what the caller saw (a hit and its value, a factory
/// call, a fail-safe value or an error); the two records must be identical.
/// </summary>
/// <remarks>
/// <para>The rules the model encodes (see docs/features/expiration.md and fail-safe.md):</para>
/// <list type="bullet">
/// <item>An entry is fresh until <c>Duration</c> (or <c>DefaultExpiration</c>) after it was stored; then the next load
/// calls the factory.</item>
/// <item>Sliding expiration: an entry nobody reads expires one window after its last read, and reads never keep it
/// past its <c>Duration</c> (without a Duration, <c>MaxExpiration</c>).</item>
/// <item>Fail-safe: when the factory throws, an expired value is served for <c>FailSafe</c> after expiry, and then
/// held for <c>FailSafeRetryAfter</c>, never extended by sliding reads. Without a usable value the error reaches
/// the caller and nothing is cached.</item>
/// <item>Invalidating a key, a tag or a tag pattern removes the matching entries; the next load calls the factory.</item>
/// </list>
/// <para>Legitimate differences are ruled out by the setup: capacity (10,000 entries) is far above the four keys,
/// so nothing is evicted; TTL jitter is off; there is no refresh-ahead or background cleanup timer, so nothing runs
/// outside the operations. Seeds and replay: see <see cref="PropertySettings"/>.</para>
/// </remarks>
public class MemoryCacheModelTests
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxExpiration = TimeSpan.FromHours(1);

    private static readonly string[] Keys = { "k0", "k1", "k2", "k3" };
    private static readonly string[] TagNames = { "t0", "t1", "t2" };
    private static readonly string[] Patterns = { "t*", "*1", "t?", "x*", "t2" };

    /// <summary>A policy and the settings the model reads from it. The cache keys its settings by policy instance.</summary>
    private sealed record Spec(string Name, CacheRuntimePolicy Policy)
    {
        public TimeSpan? Duration => Policy.Duration;

        public TimeSpan Sliding => Policy.SlidingExpiration ?? TimeSpan.Zero;

        public TimeSpan FailSafe => Policy.FailSafe ?? TimeSpan.Zero;

        public TimeSpan RetryAfter => Policy.FailSafeRetryAfter ?? TimeSpan.FromSeconds(30);

        public override string ToString() => Name;
    }

    private static Spec Make(string name, TimeSpan? duration = null, TimeSpan? sliding = null, TimeSpan? failSafe = null, TimeSpan? retryAfter = null, params string[] tags)
        => new(name, CacheRuntimePolicy.Empty("Model." + name) with
        {
            Duration = duration,
            SlidingExpiration = sliding,
            FailSafe = failSafe,
            FailSafeRetryAfter = retryAfter,
            Tags = tags,
        });

    private static readonly Spec[] Specs =
    {
        Make("short[t0]", duration: TimeSpan.FromSeconds(10), tags: "t0"),
        Make("long[t1,t2]", duration: TimeSpan.FromSeconds(30), tags: new[] { "t1", "t2" }),
        Make("sliding10of60[t0,t1]", duration: TimeSpan.FromSeconds(60), sliding: TimeSpan.FromSeconds(10), tags: new[] { "t0", "t1" }),
        Make("default"),
        Make("failsafe[t2]", duration: TimeSpan.FromSeconds(10), failSafe: TimeSpan.FromSeconds(60), retryAfter: TimeSpan.FromSeconds(5), tags: "t2"),
        Make("sliding-failsafe[t1]", duration: TimeSpan.FromSeconds(30), sliding: TimeSpan.FromSeconds(10), failSafe: TimeSpan.FromSeconds(60), retryAfter: TimeSpan.FromSeconds(5), tags: "t1"),
        Make("sliding-only", sliding: TimeSpan.FromSeconds(10)),
    };

    /// <summary>What <see cref="InMemoryCacheManager.SetAsync{T}"/> stores: no tags, sliding or fail-safe.</summary>
    private static readonly Spec Plain = Make("set");

    private sealed class Real
    {
        public FakeTimeProvider Time { get; } = new();

        public InMemoryCacheManager Cache { get; }

        public List<string> Log { get; } = new();

        public Real()
        {
            var options = new MethodCacheMemoryOptions
            {
                EnableBackgroundCleanup = false,
                DefaultExpiration = DefaultExpiration,
                MaxExpiration = MaxExpiration,
                JitterMaxDuration = TimeSpan.Zero,
            };
            Cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), Time);
        }
    }

    private sealed class Entry
    {
        public required int Value { get; init; }

        public required Spec Spec { get; init; }

        public required TimeSpan Logical { get; set; }

        public required TimeSpan Physical { get; set; }

        public required TimeSpan Absolute { get; set; }
    }

    private sealed class Model
    {
        public TimeSpan Now { get; set; }

        public Dictionary<string, Entry> Entries { get; } = new();

        public List<string> Log { get; } = new();

        /// <summary>The entry while it physically exists (fresh, or held as a fail-safe value).</summary>
        public Entry? Present(string key)
        {
            if (Entries.TryGetValue(key, out var entry) && Now < entry.Physical)
            {
                return entry;
            }

            Entries.Remove(key);
            return null;
        }

        /// <summary>A fresh entry, after the read extends its sliding window.</summary>
        public Entry? ReadFresh(string key)
        {
            var entry = Present(key);
            if (entry == null || Now >= entry.Logical)
            {
                return null;
            }

            if (entry.Spec.Sliding > TimeSpan.Zero)
            {
                var extended = Min(Now + entry.Spec.Sliding, entry.Absolute);
                if (extended > entry.Logical)
                {
                    entry.Logical = extended;
                    entry.Physical = extended + entry.Spec.FailSafe;
                }
            }

            return entry;
        }

        public void Store(string key, int value, Spec spec, TimeSpan? duration = null)
        {
            var explicitDuration = duration ?? spec.Duration;
            var lifetime = Min(explicitDuration ?? DefaultExpiration, MaxExpiration);
            var absolute = Now + MaxExpiration;
            if (spec.Sliding > TimeSpan.Zero)
            {
                var cap = explicitDuration != null ? lifetime : MaxExpiration;
                absolute = Now + cap;
                lifetime = Min(cap, spec.Sliding);
            }

            Entries[key] = new Entry
            {
                Value = value,
                Spec = spec,
                Logical = Now + lifetime,
                Physical = Now + lifetime + spec.FailSafe,
                Absolute = absolute,
            };
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }

    private static readonly Gen<string> GenKey = Gen.OneOfConst(Keys);
    private static readonly Gen<Spec> GenSpec = Gen.OneOfConst(Specs);
    private static readonly Gen<int> GenValue = Gen.Int[1, 999];

    private static readonly GenOperation<Real, Model> GetOrCreate =
        Gen.Select(GenKey, GenSpec, GenValue).Operation<Real, Model>(
            t => $"GetOrCreate({t.Item1}, {t.Item2}, factory={t.Item3})",
            (real, t) =>
            {
                var (key, spec, value) = t;
                var called = false;
                var result = real.Cache.GetOrCreateFastAsync(key, "M", () => { called = true; return Task.FromResult(value); }, spec.Policy)
                    .GetAwaiter().GetResult();
                real.Log.Add($"get {key}: {(called ? "load" : "hit")} {result}");
            },
            (model, t) =>
            {
                var (key, spec, value) = t;
                if (model.ReadFresh(key) is { } entry)
                {
                    model.Log.Add($"get {key}: hit {entry.Value}");
                    return;
                }

                model.Store(key, value, spec);
                model.Log.Add($"get {key}: load {value}");
            });

    private static readonly GenOperation<Real, Model> GetOrCreateFailing =
        Gen.Select(GenKey, GenSpec).Operation<Real, Model>(
            t => $"GetOrCreate({t.Item1}, {t.Item2}, factory throws)",
            (real, t) =>
            {
                var (key, spec) = t;
                var called = false;
                try
                {
                    var result = real.Cache.GetOrCreateFastAsync<int>(key, "M", () => { called = true; return Task.FromException<int>(new TimeoutException()); }, spec.Policy)
                        .GetAwaiter().GetResult();
                    real.Log.Add($"get {key}: {(called ? "fail-safe" : "hit")} {result}");
                }
                catch (TimeoutException)
                {
                    real.Log.Add($"get {key}: error");
                }
            },
            (model, t) =>
            {
                var (key, spec) = t;
                if (model.ReadFresh(key) is { } fresh)
                {
                    model.Log.Add($"get {key}: hit {fresh.Value}");
                    return;
                }

                // The factory throws: an expired value still held for fail-safe is served and held for RetryAfter
                if (spec.FailSafe > TimeSpan.Zero && model.Present(key) is { } stale)
                {
                    stale.Logical = stale.Absolute = model.Now + spec.RetryAfter;
                    model.Log.Add($"get {key}: fail-safe {stale.Value}");
                    return;
                }

                model.Log.Add($"get {key}: error");
            });

    private static readonly GenOperation<Real, Model> TryGet =
        GenKey.Operation<Real, Model>(
            key => $"TryGetFast({key})",
            (real, key) => real.Log.Add(real.Cache.TryGetFast<int>(key, out var value) ? $"peek {key}: {value}" : $"peek {key}: miss"),
            (model, key) => model.Log.Add(model.ReadFresh(key) is { } entry ? $"peek {key}: {entry.Value}" : $"peek {key}: miss"));

    private static readonly GenOperation<Real, Model> Set =
        Gen.Select(GenKey, GenValue, Gen.Int[1, 20]).Operation<Real, Model>(
            t => $"SetAsync({t.Item1}, {t.Item2}, {t.Item3}s)",
            (real, t) => real.Cache.SetAsync(t.Item1, t.Item2, TimeSpan.FromSeconds(t.Item3)).GetAwaiter().GetResult(),
            (model, t) => model.Store(t.Item1, t.Item2, Plain, TimeSpan.FromSeconds(t.Item3)));

    private static readonly GenOperation<Real, Model> InvalidateKey =
        GenKey.Operation<Real, Model>(
            key => $"InvalidateByKeys({key})",
            (real, key) => real.Cache.InvalidateByKeysAsync(key).GetAwaiter().GetResult(),
            (model, key) => model.Entries.Remove(key));

    private static readonly GenOperation<Real, Model> InvalidateTag =
        Gen.OneOfConst(TagNames).Operation<Real, Model>(
            tag => $"InvalidateByTags({tag})",
            (real, tag) => real.Cache.InvalidateByTagsAsync(tag).GetAwaiter().GetResult(),
            (model, tag) => RemoveWhere(model, tags => tags.Contains(tag)));

    private static readonly GenOperation<Real, Model> InvalidatePattern =
        Gen.OneOfConst(Patterns).Operation<Real, Model>(
            pattern => $"InvalidateByTagPattern({pattern})",
            (real, pattern) => real.Cache.InvalidateByTagPatternAsync(pattern).GetAwaiter().GetResult(),
            (model, pattern) =>
            {
                var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$");
                RemoveWhere(model, tags => tags.Any(regex.IsMatch));
            });

    private static readonly GenOperation<Real, Model> Advance =
        Gen.Frequency((3, Gen.Int[0, 15_000]), (1, Gen.OneOfConst(2_500, 5_000, 10_000))).Operation<Real, Model>(
            ms => $"Advance({ms}ms)",
            (real, ms) => real.Time.Advance(TimeSpan.FromMilliseconds(ms)),
            (model, ms) => model.Now += TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void InMemoryCacheManager_BehavesLikeTheModel()
    {
        Gen.Const(() => (new Real(), new Model()))
            .SampleModelBased(
                new[]
                {
                    GetOrCreate, GetOrCreate, GetOrCreate, GetOrCreateFailing, TryGet, Set,
                    InvalidateKey, InvalidateTag, InvalidatePattern, Advance, Advance, Advance,
                },
                equal: Equal,
                iter: PropertySettings.ModelIterations,
                printActual: real => Describe(real.Log),
                printModel: model => Describe(model.Log));
    }

    /// <summary>Same observations, and afterwards the same keys hold a fresh entry (checked without reading them).</summary>
    private static bool Equal(Real real, Model model)
    {
        if (!real.Log.SequenceEqual(model.Log))
        {
            return false;
        }

        foreach (var key in Keys)
        {
            var exists = real.Cache.ExistsAsync(key).AsTask().GetAwaiter().GetResult();
            var fresh = model.Entries.TryGetValue(key, out var entry) && model.Now < entry.Logical && model.Now < entry.Physical;
            if (exists != fresh)
            {
                real.Log.Add($"end: {key} {(exists ? "fresh" : "absent")}");
                model.Log.Add($"end: {key} {(fresh ? "fresh" : "absent")}");
                return false;
            }
        }

        return true;
    }

    private static void RemoveWhere(Model model, Func<IReadOnlyCollection<string>, bool> matches)
    {
        foreach (var (key, entry) in model.Entries.ToList())
        {
            if (matches(entry.Spec.Policy.Tags))
            {
                model.Entries.Remove(key);
            }
        }
    }

    private static string Describe(List<string> log)
    {
        var text = new StringBuilder();
        foreach (var line in log)
        {
            text.Append("\n    ").Append(line);
        }

        return text.ToString();
    }
}
