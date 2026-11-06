using MethodCache.Infrastructure;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MethodCache.Extensibility;

namespace MethodCache
{
    /// <summary>
    /// Immutable options describing how a fluent cache entry should behave.
    /// </summary>
    public sealed class CacheEntryOptions
    {
        internal static CacheEntryOptions Default { get; } = new Builder().Build();
        internal CacheEntryOptions(
            TimeSpan? duration,
            TimeSpan? slidingExpiration,
            TimeSpan? refreshAhead,
            IReadOnlyList<string> tags,
            int? version,
            TimeSpan? failSafe,
            TimeSpan? failSafeTimeout,
            TimeSpan? failSafeRetryAfter,
            bool clearsTags = false, bool? enabled = null, bool? distributed = null, Func<object?, bool>? cacheWhen = null,
            Func<object?, TimeSpan>? durationFor = null, Func<object?, string[]>? tagsFor = null)
        {
            Enabled = enabled;
            Distributed = distributed;
            CacheWhen = cacheWhen;
            DurationFor = durationFor;
            TagsFor = tagsFor;
            ClearsTags = clearsTags;
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            RefreshAhead = refreshAhead;
            Tags = tags;
            Version = version;
            FailSafe = failSafe;
            FailSafeTimeout = failSafeTimeout;
            FailSafeRetryAfter = failSafeRetryAfter;
        }

        /// <summary>
        /// Gets the absolute duration for the cache entry.
        /// </summary>
        public TimeSpan? Duration { get; }

        /// <summary>
        /// Gets the sliding expiration window for the cache entry.
        /// </summary>
        public TimeSpan? SlidingExpiration { get; }

        /// <summary>
        /// Gets the refresh-ahead window for the cache entry.
        /// </summary>
        public TimeSpan? RefreshAhead { get; }

        /// <summary>
        /// Gets the tags associated with the entry for downstream invalidation.
        /// </summary>
        public IReadOnlyList<string> Tags { get; }

        /// <summary>
        /// Gets whether <see cref="Builder.ClearTags"/> was called, so the policy of a cached method drops the tags
        /// of its attribute and of the other lower layers instead of adding to them.
        /// </summary>
        public bool ClearsTags { get; }
        /// <summary>Whether caching is enabled; unset inherits the default.</summary>
        public bool? Enabled { get; }
        /// <summary>Whether distributed caching is enabled; unset inherits the default.</summary>
        public bool? Distributed { get; }
        internal Func<object?, bool>? CacheWhen { get; }
        internal Func<object?, TimeSpan>? DurationFor { get; }
        internal Func<object?, string[]>? TagsFor { get; }

        /// <summary>
        /// Checks whether the typed result may be cached: the <c>CacheWhen</c> predicate accepts it, a
        /// <c>DurationFor</c> function gives it a positive duration, and a <c>TagsFor</c> function gives it valid tags.
        /// A throwing predicate or function rejects the result.
        /// </summary>
        /// <remarks>Custom managers apply this check both before publication and after distributed reads.</remarks>
        /// <exception cref="InvalidOperationException">The <c>CacheWhen&lt;T&gt;</c> predicate, <c>DurationFor&lt;T&gt;</c> or <c>TagsFor&lt;T&gt;</c> function is for a type that a <typeparamref name="T"/> result can never be.</exception>
        public bool CanCache<T>(T value)
        {
            if (Enabled == false) return false;
            CachePredicateCheck.EnsureMatches(CacheWhen, typeof(T), "this call");
            try { if (CacheWhen?.Invoke(value) == false) return false; }
            catch (Exception) { return false; }
            if (DurationFor != null && DurationOf(value) <= TimeSpan.Zero) return false;
            if (TagsFor == null) return true;
            CachePredicateCheck.EnsureMatches(TagsFor, typeof(T), "this call");
            try { _ = TagsFor(value); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Gets the tags <paramref name="value"/> is stored with: <see cref="Tags"/> and those the <c>TagsFor</c> function
        /// gives it, without duplicates. Just <see cref="Tags"/> without a function, or when it throws, in which case
        /// <see cref="CanCache{T}"/> rejects the value.
        /// </summary>
        /// <remarks>Custom managers store the entry with these tags in place of <see cref="Tags"/>.</remarks>
        /// <exception cref="InvalidOperationException">The <c>TagsFor&lt;T&gt;</c> function is for a type that a <typeparamref name="T"/> result can never be.</exception>
        public IReadOnlyList<string> TagsOf<T>(T value)
        {
            if (TagsFor == null) return Tags;
            CachePredicateCheck.EnsureMatches(TagsFor, typeof(T), "this call");
            string[] given;
            try { given = TagsFor(value); }
            catch (Exception) { return Tags; }
            return given.Length == 0 ? Tags : Array.AsReadOnly(Tags.Concat(given).Distinct(StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// Gets how long <paramref name="value"/> is cached: what the <c>DurationFor</c> function gives it, else
        /// <see cref="Duration"/>. Null when neither is set, so the manager's default applies; <see cref="TimeSpan.Zero"/>
        /// when the function gives zero or less, or throws, and the value isn't cached.
        /// </summary>
        /// <remarks>Custom managers use this as the entry's lifetime in place of <see cref="Duration"/>.</remarks>
        /// <exception cref="InvalidOperationException">The <c>DurationFor&lt;T&gt;</c> function is for a type that a <typeparamref name="T"/> result can never be.</exception>
        public TimeSpan? DurationOf<T>(T value)
        {
            if (DurationFor == null) return Duration;
            CachePredicateCheck.EnsureMatches(DurationFor, typeof(T), "this call");
            try { return DurationFor(value) is var duration && duration > TimeSpan.Zero ? duration : TimeSpan.Zero; }
            catch (Exception) { return TimeSpan.Zero; }
        }
        /// <summary>The policy of a generated call, whose key is already complete; null for options built by the application.</summary>
        internal CacheRuntimePolicy? Runtime { get; init; }

        /// <summary>
        /// Options for a call with a prepared key: a generated call's key already carries its version, so
        /// <see cref="Version"/> stays unset and an application manager does not add it a second time. The administrator's
        /// view of a method (<paramref name="preparedKey"/> false) keeps the version.
        /// </summary>
        internal static CacheEntryOptions FromRuntime(CacheRuntimePolicy policy, bool preparedKey = true) => new(
            policy.Duration, policy.SlidingExpiration, policy.RefreshAhead,
            Array.AsReadOnly(policy.Tags.ToArray()), preparedKey ? null : policy.Version,
            policy.FailSafe, policy.FailSafeTimeout, policy.FailSafeRetryAfter,
            clearsTags: policy.Fields.HasFlag(Policies.CachePolicyFields.ClearTags),
            enabled: policy.Enabled, distributed: policy.Distributed, cacheWhen: policy.CacheWhen, durationFor: policy.DurationFor,
            tagsFor: policy.TagsFor)
        { Runtime = preparedKey ? policy : null };

        /// <summary>
        /// Gets the version that is added to the key, so changing it skips entries stored under the old one. A manager
        /// stores the entry under <see cref="MethodCacheKeys.StoredKey"/> of its key and this version. It is
        /// <c>null</c> in the options of a call from a cached method, whose key already carries the method's version.
        /// </summary>
        public int? Version { get; }

        /// <summary>
        /// Gets how long an expired value is kept as a fail-safe fallback; <see cref="TimeSpan.Zero"/> turns it off.
        /// </summary>
        public TimeSpan? FailSafe { get; }

        /// <summary>
        /// Gets how long a reload may take before the caller gets the fail-safe value instead.
        /// </summary>
        public TimeSpan? FailSafeTimeout { get; }

        /// <summary>
        /// Gets how long a served fail-safe value is reused before the method is called again.
        /// </summary>
        public TimeSpan? FailSafeRetryAfter { get; }

        /// <summary>
        /// Builder for <see cref="CacheEntryOptions"/> instances.
        /// </summary>
        public sealed class Builder
        {
            private bool? _enabled;
            private bool? _distributed;
            private Func<object?, bool>? _cacheWhen;
            private Func<object?, TimeSpan>? _durationFor;
            private Func<object?, string[]>? _tagsFor;
            /// <summary>Enables or disables all caching for the invocation or method.</summary>
            public Builder WithEnabled(bool enabled) { _enabled = enabled; return this; }
            /// <summary>Enables or disables distributed reads and writes.</summary>
            public Builder WithDistributed(bool distributed) { _distributed = distributed; return this; }
            /// <summary>Stores and accepts only results satisfying the typed predicate.</summary>
            public Builder CacheWhen<T>(Func<T, bool> predicate)
            {
                ArgumentNullException.ThrowIfNull(predicate);
                _cacheWhen = new TypedCachePredicate<T>(predicate).Accept;
                return this;
            }

            /// <summary>
            /// Gives each result its own duration, such as the time left on a token:
            /// <c>DurationFor&lt;Token&gt;(t =&gt; t.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))</c>. It replaces
            /// <see cref="WithDuration"/>; whichever is called last wins. A duration of zero or less, or a function that
            /// throws, doesn't cache the result. No jitter is added, and <c>MaxExpiration</c> still caps it.
            /// </summary>
            /// <exception cref="ArgumentNullException"><paramref name="duration"/> is <c>null</c>.</exception>
            public Builder DurationFor<T>(Func<T, TimeSpan> duration)
            {
                ArgumentNullException.ThrowIfNull(duration);
                _durationFor = new TypedDuration<T>(duration).For;
                _duration = null;
                return this;
            }

            /// <summary>
            /// Adds tags read from each result to its entry, next to the tags of <see cref="AddTags"/>, such as the
            /// customer of an order: <c>TagsFor&lt;Order&gt;(o =&gt; [$"customer:{o.CustomerId}"])</c>. Invalidating one of
            /// them removes the entry. A function that throws, or gives a null, empty or whitespace tag, doesn't cache
            /// the result. Calling it again replaces the function; <see cref="ClearTags"/> removes it.
            /// </summary>
            /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <c>null</c>.</exception>
            public Builder TagsFor<T>(Func<T, IEnumerable<string>> tags)
            {
                ArgumentNullException.ThrowIfNull(tags);
                _tagsFor = new TypedTags<T>(tags).For;
                return this;
            }

            private TimeSpan? _duration;
            private TimeSpan? _slidingExpiration;
            private TimeSpan? _refreshAhead;
            private readonly List<string> _tags = new();
            private bool _clearTags;
            private int? _version;
            private TimeSpan? _failSafe;
            private TimeSpan? _failSafeTimeout;
            private TimeSpan? _failSafeRetryAfter;

            /// <summary>
            /// Sets the cache duration. It replaces <see cref="DurationFor{T}"/>; whichever is called last wins.
            /// </summary>
            public Builder WithDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(duration),
                        $"Cache duration must be positive. Provided: {duration}. " +
                        "Without a duration, entries use the default set with Defaults(o => o.WithDuration(...)) (5 minutes unless configured); " +
                        "no entry lives longer than MethodCacheMemoryOptions.MaxExpiration. " +
                        "Common durations: TimeSpan.FromMinutes(5), TimeSpan.FromHours(1). " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#duration");
                }

                _duration = duration;
                _durationFor = null;
                return this;
            }

            /// <summary>
            /// Adds one or more tags to the cache entry. For a cached method (startup code or a runtime override),
            /// they are added to the tags of its <c>[Cache]</c> attribute and of the other layers, so
            /// <c>[CacheInvalidate]</c> still reaches the entry; call <see cref="ClearTags"/> first to replace them.
            /// </summary>
            /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <c>null</c>.</exception>
            /// <exception cref="ArgumentException">
            /// A tag is <c>null</c>, empty or whitespace, which <see cref="ICacheManager.InvalidateTagsAsync"/> rejects
            /// too, so no invalidation could ever reach it. No tag is added then.
            /// </exception>
            public Builder AddTags(params string[] tags)
            {
                ArgumentNullException.ThrowIfNull(tags);
                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        throw new ArgumentException("A cache tag cannot be null or whitespace.", nameof(tags));
                    }
                }

                _tags.AddRange(tags);
                return this;
            }

            /// <summary>
            /// Removes the tags added so far, and the <see cref="TagsFor{T}"/> function. For a cached method, it also drops
            /// the tags of its <c>[Cache]</c> attribute, of appsettings, of the defaults and a <c>TagsFor</c> set in a lower
            /// layer, so only tags added after this call apply. An entry
            /// without those tags is no longer removed by a <c>[CacheInvalidate]</c> that lists them.
            /// </summary>
            public Builder ClearTags()
            {
                _tags.Clear();
                _tagsFor = null;
                _clearTags = true;
                return this;
            }

            /// <summary>
            /// Sets the sliding expiration window for the cache entry. <see cref="TimeSpan.Zero"/> turns sliding
            /// expiration off, for example for one method when it is on for all of them.
            /// </summary>
            public Builder WithSlidingExpiration(TimeSpan slidingExpiration)
            {
                if (slidingExpiration < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(slidingExpiration),
                        $"Sliding expiration cannot be negative; use TimeSpan.Zero to turn it off. Provided: {slidingExpiration}. " +
                        "Sliding expiration resets the timer on each cache access. " +
                        "Example: TimeSpan.FromMinutes(10) keeps frequently-accessed items cached. " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#sliding-expiration");
                }

                _slidingExpiration = slidingExpiration;
                return this;
            }

            /// <summary>
            /// Reloads the value in the background when a hit arrives within <paramref name="window"/> of its expiry.
            /// <see cref="TimeSpan.Zero"/> turns refresh-ahead off, for example for one method when it is on for all of them.
            /// </summary>
            public Builder WithRefreshAhead(TimeSpan window)
            {
                if (window < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(window), window, "Refresh window cannot be negative; use TimeSpan.Zero to turn refresh-ahead off.");
                }

                _refreshAhead = window;
                return this;
            }

            /// <summary>
            /// Keeps expired values for <paramref name="duration"/> and returns them when reloading throws, so a failing
            /// dependency degrades to slightly stale data instead of errors. <see cref="TimeSpan.Zero"/> turns fail-safe
            /// off, for example for one method when it is on for all of them. <see cref="WithFailSafeTimeout"/> and
            /// <see cref="WithFailSafeRetryAfter"/> tune it.
            /// </summary>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
            public Builder WithFailSafe(TimeSpan duration)
            {
                if (duration < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(duration), duration, "Fail-safe duration cannot be negative; use TimeSpan.Zero to turn fail-safe off.");
                }

                _failSafe = duration;
                return this;
            }

            /// <summary>
            /// With fail-safe on, a reload that takes longer than <paramref name="timeout"/> returns the old value and
            /// finishes in the background.
            /// </summary>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero or negative.</exception>
            public Builder WithFailSafeTimeout(TimeSpan timeout)
            {
                if (timeout <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Fail-safe timeout must be positive.");
                }

                _failSafeTimeout = timeout;
                return this;
            }

            /// <summary>
            /// After fail-safe returned an old value, reuses it for <paramref name="retryAfter"/> (30 seconds unless
            /// set) before the method is called again.
            /// </summary>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="retryAfter"/> is zero or negative.</exception>
            public Builder WithFailSafeRetryAfter(TimeSpan retryAfter)
            {
                if (retryAfter <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "Fail-safe retry interval must be positive.");
                }

                _failSafeRetryAfter = retryAfter;
                return this;
            }

            /// <summary>
            /// Sets a version that becomes part of the key, so entries cached under an older version are ignored.
            /// </summary>
            public Builder WithVersion(int version)
            {
                if (version < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(version), "Version must be non-negative.");
                }

                _version = version;
                return this;
            }

            /// <summary>
            /// Builds an immutable <see cref="CacheEntryOptions"/> instance.
            /// </summary>
            /// <exception cref="InvalidOperationException">The refresh window is not shorter than the duration or the sliding expiration.</exception>
            public CacheEntryOptions Build()
            {
                // A window as long as the entry lives would reload the value on every hit
                // Zero means off for both, so it never conflicts
                var refreshOn = _refreshAhead > TimeSpan.Zero;
                var tooLongForDuration = refreshOn && _refreshAhead >= _duration;
                if (tooLongForDuration || (refreshOn && _slidingExpiration > TimeSpan.Zero && _refreshAhead >= _slidingExpiration))
                {
                    var lifetime = tooLongForDuration ? $"Duration ({_duration})" : $"SlidingExpiration ({_slidingExpiration})";
                    throw new InvalidOperationException(
                        $"RefreshAhead ({_refreshAhead}) must be shorter than {lifetime}, or every hit would reload the value. " +
                        "See: https://github.com/eoniclabs/MethodCache/blob/main/docs/features/expiration.md#refresh-ahead");
                }

                return new CacheEntryOptions(
                    _duration,
                    _slidingExpiration,
                    _refreshAhead,
                    new ReadOnlyCollection<string>(_tags.ToArray()),
                    _version,
                    _failSafe,
                    _failSafeTimeout,
                    _failSafeRetryAfter,
                    _clearTags, _enabled, _distributed, _cacheWhen, _durationFor, _tagsFor);
            }
        }
    }
}
