using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Policies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

/// <summary>
/// Makes the runtime overrides apply on every instance when the cache has a distributed cache and a backplane. An
/// override is stored in the distributed cache, one record per method, before it applies here. Applying it
/// invalidates the method on every instance, as any override does, and an instance that hears of the invalidation
/// reads the method's record again. An instance that starts reads the records of all its cached methods before it
/// serves a policy, so overrides outlive restarts and new instances.
/// </summary>
/// <remarks>
/// Writing a record and applying records take one lock, but reading them doesn't, so a read the distributed cache is
/// slow to answer never holds up an override made here. Each method has a version that every change to its override
/// here raises; a record read while the version changed is read again, so a record read earlier never replaces one
/// applied later. When the distributed cache doesn't answer, an instance keeps the overrides it has, logs a warning
/// and reads all records again after <see cref="DistributedCacheLayer.RetryInterval"/>.
/// </remarks>
internal sealed class SharedRuntimeOverrides : IDisposable
{
    /// <summary>The settings that are functions, which can't be stored for another instance to run.</summary>
    internal const CachePolicyFields FunctionFields =
        CachePolicyFields.CacheWhen | CachePolicyFields.DurationFor | CachePolicyFields.TagsFor | CachePolicyFields.SlidingFor;

    // What a record can carry; Metadata is rebuilt from the audit fields
    private const CachePolicyFields StoredFields =
        CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.SlidingExpiration | CachePolicyFields.Version |
        CachePolicyFields.RefreshAhead | CachePolicyFields.FailSafe | CachePolicyFields.FailSafeTimeout |
        CachePolicyFields.FailSafeRetryAfter | CachePolicyFields.ClearTags | CachePolicyFields.Enabled | CachePolicyFields.Distributed;

    private const int RecordVersion = 1;
    private const int MaxParallelReads = 8;

    private readonly DistributedCacheLayer _layer;
    private readonly RuntimePolicyStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private readonly object _timerSync = new();

    // The revision of the record each method's override came from, so a record already applied isn't applied again
    private readonly ConcurrentDictionary<string, string> _applied = new(StringComparer.Ordinal);
    // Raised, holding _gate, by every change to a method's override here
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _cachedMethods = new HashSet<string>();
    private PolicyResolver? _resolver;
    private volatile bool _started;
    private ITimer? _retryTimer;
    private int _disposed;

    public SharedRuntimeOverrides(DistributedCacheLayer layer, RuntimePolicyStore store, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The registered instance, or null without a distributed cache. Also null for a distributed cache that can't be
    /// used, which fails its own startup check and the cache manager with a message that says why.
    /// </summary>
    internal static SharedRuntimeOverrides? From(IServiceProvider services)
        => services.GetKeyedService<IDistributedCache>(MethodCache.Infrastructure.OwnedServices.Key) is IAtomicInvalidationStore
            ? services.GetService<SharedRuntimeOverrides>()
            : null;

    /// <summary>Whether overrides apply on every instance: there is a backplane to tell the others.</summary>
    public bool Enabled => _layer.HasBackplane;

    /// <summary>
    /// Reads the records of <paramref name="cachedMethods"/> and applies them. Called once, when the policies are first
    /// read. With <paramref name="wait"/>, when the host starts, it returns once they apply or the distributed cache
    /// failed to answer within its timeout; otherwise, on a first call, it doesn't hold the call up.
    /// </summary>
    internal void Start(IReadOnlySet<string> cachedMethods, PolicyResolver resolver, bool wait)
    {
        _cachedMethods = cachedMethods;
        _resolver = resolver;
        _started = true;
        if (wait) ReloadAsync(atStart: true).ConfigureAwait(false).GetAwaiter().GetResult();
        else Observe(Task.Run(() => ReloadAsync(atStart: true)));
    }

    /// <summary>
    /// Stores the override, then applies it here. Throws, and changes nothing here, when the distributed cache
    /// doesn't answer.
    /// </summary>
    internal async Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields, RuntimeOverrideMetadata? metadata, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var revision = Guid.NewGuid().ToString("N");
            if (metadata?.ExpiresAt is { } expiresAt && expiresAt <= now)
                await StoreAsync(_layer.RemoveOverrideAsync(methodId, CancellationToken.None), methodId).ConfigureAwait(false);
            else
                await StoreAsync(_layer.WriteOverrideAsync(methodId, Encode(revision, now, policy, fields, metadata), metadata?.ExpiresAt, CancellationToken.None), methodId).ConfigureAwait(false);

            Changed(methodId);
            _applied[methodId] = revision;
            await _store.UpsertAsync(methodId, policy, fields, metadata, now).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the stored override, then the one here. Throws, and changes nothing here, when the distributed cache doesn't answer.</summary>
    internal async Task RemoveAsync(string methodId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StoreAsync(_layer.RemoveOverrideAsync(methodId, CancellationToken.None), methodId).ConfigureAwait(false);
            Changed(methodId);
            _applied.TryRemove(methodId, out _);
            await _store.RemoveAsync(methodId).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Throws one exception type for every way the distributed cache can fail. A write that failed may still have
    /// landed, or a timed-out one land later, so this instance reads every record again after
    /// <see cref="DistributedCacheLayer.RetryInterval"/>; applying one that landed tells the others.
    /// </summary>
    private async Task StoreAsync(Task write, string methodId)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_started) ScheduleRetry();
            throw new InvalidOperationException(
                $"MethodCache could not store the runtime override of '{methodId}' in the distributed cache, so nothing changed on this instance. Try again once the distributed cache answers.", ex);
        }
    }

    /// <summary>Another instance invalidated <paramref name="methodId"/>, perhaps because its override changed.</summary>
    internal void OnRemoteMethodInvalidated(string methodId)
    {
        if (!_started || !_cachedMethods.Contains(methodId) || Volatile.Read(ref _disposed) != 0) return;
        // Off the backplane's thread, which delivers the next message only when this one returns
        Observe(Task.Run(() => RefreshAsync(methodId)));
    }

    /// <summary>
    /// Another instance cleared the cache, or the backplane may have lost messages, so every record is read again.
    /// </summary>
    internal void OnRemoteCleared()
    {
        if (!_started || Volatile.Read(ref _disposed) != 0) return;
        Observe(Task.Run(() => ReloadAsync(atStart: false)));
    }

    private long Version(string methodId) => _versions.GetValueOrDefault(methodId);

    // Called holding _gate
    private void Changed(string methodId) => _versions.AddOrUpdate(methodId, 1, static (_, version) => version + 1);

    private async Task RefreshAsync(string methodId)
    {
        while (true)
        {
            var seen = Version(methodId);
            byte[]? record;
            try
            {
                record = await _layer.ReadOverrideAsync(methodId, _disposing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_disposing.IsCancellationRequested)
            {
                _logger.LogWarning("MethodCache could not read the runtime override of '{MethodId}' from the distributed cache after another instance changed the method; it reads every override again in {Interval}. Error type: {ErrorType}.",
                    methodId, _layer.RetryInterval, ex.GetType().FullName);
                ScheduleRetry();
                return;
            }

            await _gate.WaitAsync(_disposing.Token).ConfigureAwait(false);
            try
            {
                // Changed here while the record was read: it may be older than what applies now
                if (Version(methodId) != seen) continue;
                await ApplyAsync(methodId, record).ConfigureAwait(false);
                return;
            }
            finally { _gate.Release(); }
        }
    }

    private async Task ReloadAsync(bool atStart)
    {
        var methods = _cachedMethods.Order(StringComparer.Ordinal).ToArray();
        if (methods.Length == 0) return;
        var seen = methods.Select(Version).ToArray();
        var records = new byte[]?[methods.Length];
        try
        {
            // One timeout per round of parallel reads; the first failure cancels the others
            var rounds = (methods.Length + MaxParallelReads - 1) / MaxParallelReads;
            using var deadline = new CancellationTokenSource(_layer.OperationTimeout * rounds, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _disposing.Token);
            await Parallel.ForEachAsync(Enumerable.Range(0, methods.Length),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = linked.Token },
                async (index, token) => records[index] = await _layer.ReadOverrideAsync(methods[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_disposing.IsCancellationRequested)
        {
            _logger.LogWarning(atStart
                    ? "MethodCache could not read the runtime overrides from the distributed cache, so this instance starts without them and reads them again in {Interval}. Error type: {ErrorType}."
                    : "MethodCache could not read the runtime overrides from the distributed cache, so this instance keeps the ones it has and reads them again in {Interval}. Error type: {ErrorType}.",
                _layer.RetryInterval, ex.GetType().FullName);
            ScheduleRetry();
            return;
        }

        List<string>? stale = null;
        await _gate.WaitAsync(_disposing.Token).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < methods.Length; index++)
            {
                if (Version(methods[index]) != seen[index]) (stale ??= []).Add(methods[index]);
                else await ApplyAsync(methods[index], records[index]).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }

        // Changed here while they were read: read them again
        foreach (var method in stale ?? []) await RefreshAsync(method).ConfigureAwait(false);
    }

    // Called holding _gate
    private async Task ApplyAsync(string methodId, byte[]? record)
    {
        if (record == null)
        {
            var hadRecord = _applied.TryRemove(methodId, out _);
            if (!hadRecord && !_store.Contains(methodId)) return;
            Changed(methodId);
            await _store.RemoveAsync(methodId).ConfigureAwait(false);
            return;
        }

        if (!TryDecode(record, out var stored))
        {
            _logger.LogWarning("The runtime override of '{MethodId}' in the distributed cache was written in a format this version of MethodCache can't read, so it doesn't apply on this instance.", methodId);
            return;
        }

        if (_applied.TryGetValue(methodId, out var applied) && applied == stored.Revision) return;
        Changed(methodId);
        _applied[methodId] = stored.Revision;
        if (_resolver?.FindOverrideContradiction(methodId, stored.Policy, stored.Fields) is { } contradiction)
        {
            _logger.LogWarning("The runtime override of '{MethodId}' doesn't apply on this instance: {Reason}", methodId, contradiction);
            return;
        }

        if (_resolver?.FindOverrideTagProblem(methodId, stored.Policy, stored.Fields) is { } problem)
        {
            _logger.LogWarning("The runtime override of '{MethodId}' doesn't apply on this instance: a tag in it reaches the cached method '{Reached}', but {Reason}.",
                methodId, problem.MethodId, problem.ValueFreeReason);
            return;
        }

        await _store.UpsertAsync(methodId, stored.Policy, stored.Fields, stored.Metadata, stored.UpdatedAt).ConfigureAwait(false);
    }

    private void ScheduleRetry()
    {
        lock (_timerSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _retryTimer != null) return;
            _retryTimer = _time.CreateTimer(static state =>
            {
                var shared = (SharedRuntimeOverrides)state!;
                lock (shared._timerSync)
                {
                    shared._retryTimer?.Dispose();
                    shared._retryTimer = null;
                }

                shared.Observe(shared.ReloadAsync(atStart: false));
            }, this, _layer.RetryInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Observe(Task task)
        => _ = task.ContinueWith(static (task, state) =>
        {
            if (task.Exception?.GetBaseException() is { } error && error is not OperationCanceledException)
                ((SharedRuntimeOverrides)state!)._logger.LogWarning("MethodCache could not apply a runtime override from the distributed cache. Error type: {ErrorType}.", error.GetType().FullName);
        }, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerSync)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        _disposing.Cancel();
        _disposing.Dispose();
    }

    // ---------- record format ----------

    internal readonly record struct StoredOverride(string Revision, DateTimeOffset UpdatedAt, CachePolicy Policy, CachePolicyFields Fields, RuntimeOverrideMetadata? Metadata);

    internal static byte[] Encode(string revision, DateTimeOffset updatedAt, CachePolicy policy, CachePolicyFields fields, RuntimeOverrideMetadata? metadata)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", RecordVersion);
            writer.WriteString("revision", revision);
            writer.WriteString("updatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("fields", (int)(fields & StoredFields));
            Time("duration", policy.Duration);
            Time("slidingExpiration", policy.SlidingExpiration);
            Time("refreshAhead", policy.RefreshAhead);
            Time("failSafe", policy.FailSafe);
            Time("failSafeTimeout", policy.FailSafeTimeout);
            Time("failSafeRetryAfter", policy.FailSafeRetryAfter);
            if (policy.Version is { } version) writer.WriteNumber("version", version);
            if (policy.Enabled is { } enabled) writer.WriteBoolean("enabled", enabled);
            if (policy.Distributed is { } distributed) writer.WriteBoolean("distributed", distributed);
            writer.WriteStartArray("tags");
            foreach (var tag in policy.Tags) writer.WriteStringValue(tag);
            writer.WriteEndArray();
            if (metadata != null)
            {
                writer.WriteStartObject("metadata");
                if (metadata.Owner != null) writer.WriteString("owner", metadata.Owner);
                if (metadata.Reason != null) writer.WriteString("reason", metadata.Reason);
                if (metadata.Ticket != null) writer.WriteString("ticket", metadata.Ticket);
                if (metadata.ExpiresAt is { } expiresAt) writer.WriteString("expiresAt", expiresAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            void Time(string name, TimeSpan? value)
            {
                if (value is { } time) writer.WriteNumber(name, time.Ticks);
            }
        }

        return buffer.ToArray();
    }

    internal static bool TryDecode(byte[] record, out StoredOverride stored)
    {
        stored = default;
        try
        {
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || version.GetInt32() != RecordVersion)
                return false;

            var tags = root.TryGetProperty("tags", out var list) ? list.EnumerateArray().Select(static tag => tag.GetString()!).ToArray() : [];
            var policy = CachePolicy.Empty with
            {
                Duration = Time("duration"),
                SlidingExpiration = Time("slidingExpiration"),
                RefreshAhead = Time("refreshAhead"),
                FailSafe = Time("failSafe"),
                FailSafeTimeout = Time("failSafeTimeout"),
                FailSafeRetryAfter = Time("failSafeRetryAfter"),
                Version = root.TryGetProperty("version", out var number) ? number.GetInt32() : null,
                Enabled = root.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : null,
                Distributed = root.TryGetProperty("distributed", out var distributed) ? distributed.GetBoolean() : null,
                Tags = tags,
            };

            RuntimeOverrideMetadata? metadata = null;
            if (root.TryGetProperty("metadata", out var audit))
            {
                metadata = new RuntimeOverrideMetadata
                {
                    Owner = Text(audit, "owner"),
                    Reason = Text(audit, "reason"),
                    Ticket = Text(audit, "ticket"),
                    ExpiresAt = Text(audit, "expiresAt") is { } expiresAt ? DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                };
            }

            stored = new StoredOverride(
                root.GetProperty("revision").GetString()!,
                DateTimeOffset.Parse(root.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                policy,
                (CachePolicyFields)root.GetProperty("fields").GetInt32() & StoredFields,
                metadata);
            return true;

            TimeSpan? Time(string name) => root.TryGetProperty(name, out var ticks) ? TimeSpan.FromTicks(ticks.GetInt64()) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return false;
        }

        static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
