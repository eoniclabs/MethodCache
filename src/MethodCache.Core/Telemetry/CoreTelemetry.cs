using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Telemetry;

internal enum RequestResult : byte { Hit, Miss, Stale, Revalidated, Bypass, Error }

internal readonly record struct CacheObservation(long LocalMethodEntries, long LocalHttpEntries,
    long LocalHttpBytes, long ObservedDistributedEntries, long TotalWeight);

// One instance per container. The container's IMeterFactory owns the meter; this service owns its source and callbacks.
internal sealed class CoreTelemetry : IDisposable
{
    private readonly Counter<long> _requests;
    private readonly Counter<long> _invalidations;
    private readonly Histogram<double> _loads;
    private readonly Histogram<double> _distributed;
    private readonly TimeProvider _time;
    private readonly bool _recordKeys;
    private readonly bool _hashKeys;
    private readonly bool _recordTags;
    private Func<CacheObservation>? _observeMethods;
    private Func<CacheObservation>? _observeHttp;
    private Func<InvalidationQueues>? _observeQueues;
    private int _disposed;
    internal ActivitySource Source { get; }

    internal CoreTelemetry(IMeterFactory factory, IOptions<MethodCacheTelemetryOptions> options, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var configured = options.Value;
        _recordKeys = configured.RecordCacheKeys;
        _hashKeys = configured.HashCacheKeys;
        _recordTags = configured.RecordCacheTags;
        var version = typeof(MethodCacheTelemetry).Assembly.GetName().Version?.ToString();
        var meter = factory.Create(MethodCacheTelemetry.Name, version);
        Source = new ActivitySource(MethodCacheTelemetry.Name, version);
        _requests = meter.CreateCounter<long>("methodcache.requests", "{request}");
        _loads = meter.CreateHistogram<double>("methodcache.load.duration", "s");
        _invalidations = meter.CreateCounter<long>("methodcache.invalidations", "{invalidation}");
        _distributed = meter.CreateHistogram<double>("methodcache.distributed.duration", "s");
        meter.CreateObservableUpDownCounter("methodcache.entries", ObserveEntries, "{entry}");
        meter.CreateObservableUpDownCounter("methodcache.http.size", () => Observe().LocalHttpBytes, "By");
        meter.CreateObservableUpDownCounter("methodcache.distributed.queue", ObserveQueues, "{invalidation}");
    }

    internal bool RequestsEnabled => _requests.Enabled;
    internal bool Enabled => _requests.Enabled || _loads.Enabled || Source.HasListeners();

    internal async Task<T> OriginAsync<T>(string name, string key, IReadOnlyCollection<string>? tags, Func<Task<T>> factory)
    {
        using var operation = BeginLoad("method", name, key, tags);
        try { return await factory().ConfigureAwait(false); }
        catch (Exception error) { operation?.Fail(error); throw; }
    }

    internal async Task<T> BypassAsync<T>(string name, string key, Func<Task<T>> factory)
    {
        var result = RequestResult.Bypass;
        try { return await OriginAsync(name, key, null, factory).ConfigureAwait(false); }
        catch { result = RequestResult.Error; throw; }
        finally { Request("method", name, result); }
    }

    internal T Bypass<T>(string name, string key, Func<T> factory)
    {
        var result = RequestResult.Bypass;
        using var origin = BeginLoad("method", name, key);
        try { return factory(); }
        catch (Exception error) { result = RequestResult.Error; origin?.Fail(error); throw; }
        finally { Request("method", name, result); }
    }

    internal async Task<T> CustomAsync<T>(ICacheManager manager, string key, CacheRuntimePolicy policy,
        Func<Task<T>> factory, CancellationToken token)
    {
        var ran = false;
        var outcome = RequestResult.Error;
        try
        {
            var value = await manager.GetOrCreateFastAsync(key, policy.MethodId, () =>
            {
                ran = true;
                return OriginAsync(policy.MethodId, key, policy.Tags, factory);
            }, policy, token).ConfigureAwait(false);
            outcome = manager.GetType() == typeof(MethodCache.Extensibility.NoOpCacheManager) ? RequestResult.Bypass
                : ran ? RequestResult.Miss : RequestResult.Hit;
            return value;
        }
        finally { Request("method", policy.MethodId, outcome); }
    }

    internal void ObserveMethods(Func<CacheObservation> observe) => Volatile.Write(ref _observeMethods, observe);
    internal void ObserveHttp(Func<CacheObservation> observe) => Volatile.Write(ref _observeHttp, observe);
    internal void ObserveRecovery(Func<InvalidationQueues> observe) => Volatile.Write(ref _observeQueues, observe);

    internal CacheObservation Observe()
    {
        if (Volatile.Read(ref _disposed) != 0) return default;
        var methods = Volatile.Read(ref _observeMethods)?.Invoke() ?? default;
        var http = Volatile.Read(ref _observeHttp)?.Invoke() ?? default;
        return new CacheObservation(methods.LocalMethodEntries, http.LocalHttpEntries, http.LocalHttpBytes,
            http.ObservedDistributedEntries, methods.TotalWeight);
    }

    private IEnumerable<Measurement<long>> ObserveEntries()
    {
        var current = Observe();
        yield return new Measurement<long>(current.LocalMethodEntries, new KeyValuePair<string, object?>("kind", "method"));
        yield return new Measurement<long>(current.LocalHttpEntries, new KeyValuePair<string, object?>("kind", "http"));
    }

    private IEnumerable<Measurement<long>> ObserveQueues()
    {
        var queues = Volatile.Read(ref _disposed) == 0 ? Volatile.Read(ref _observeQueues)?.Invoke() ?? default : default;
        yield return new Measurement<long>(queues?.Distributed ?? 0, new KeyValuePair<string, object?>("stage", "storage"));
        yield return new Measurement<long>(queues?.Publication ?? 0, new KeyValuePair<string, object?>("stage", "publication"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Request(string kind, string name, RequestResult result)
    {
        if (_requests.Enabled)
            _requests.Add(1, new("kind", kind), new("name", name), new("result", ResultName(result)));
    }

    internal void Invalidations(string type, string stage, long targets)
    {
        if (targets != 0 && _invalidations.Enabled)
            _invalidations.Add(targets, new("type", type), new("stage", stage));
    }

    internal TelemetryOperation? BeginLoad(string kind, string name, string? key = null, IReadOnlyCollection<string>? tags = null)
    {
        if (!_loads.Enabled && !Source.HasListeners()) return null;
        var activity = Source.StartActivity("MethodCache.load", ActivityKind.Internal);
        activity?.SetTag("kind", kind).SetTag("name", name);
        RecordIdentities(activity, key, tags);
        return new TelemetryOperation(_loads.Enabled ? _loads : null, _time, activity,
            new TagList { { "kind", kind }, { "name", name } });
    }

    internal TelemetryOperation? BeginDistributed(string operation)
    {
        if (!_distributed.Enabled && !Source.HasListeners()) return null;
        var activity = Source.StartActivity("MethodCache.distributed", ActivityKind.Client);
        activity?.SetTag("operation", operation);
        return new TelemetryOperation(_distributed.Enabled ? _distributed : null, _time, activity,
            new TagList { { "operation", operation } });
    }

    internal Activity? BeginInvalidation(string type, string[]? keys = null, string[]? tags = null)
    {
        if (!Source.HasListeners()) return null;
        var activity = Source.StartActivity("MethodCache.invalidate", ActivityKind.Internal);
        activity?.SetTag("type", type);
        if (activity?.IsAllDataRequested == true)
        {
            if (_recordKeys && keys != null) activity.SetTag("methodcache.keys", keys.Select(RecordedKey).ToArray());
            if (_recordTags && tags != null) activity.SetTag("methodcache.tags", tags as string[] ?? tags.ToArray());
        }
        return activity;
    }

    private void RecordIdentities(Activity? activity, string? key, IReadOnlyCollection<string>? tags)
    {
        if (activity?.IsAllDataRequested != true) return;
        if (_recordKeys && key != null) activity.SetTag("methodcache.key", RecordedKey(key));
        if (_recordTags && tags != null) activity.SetTag("methodcache.tags", tags as string[] ?? tags.ToArray());
    }

    private string RecordedKey(string key) => _hashKeys ? Convert.ToHexString(SHA256.HashData(LosslessUtf16.GetBytes(key))) : key;

    private static string ResultName(RequestResult result) => result switch
    {
        RequestResult.Hit => "hit",
        RequestResult.Miss => "miss",
        RequestResult.Stale => "stale",
        RequestResult.Revalidated => "revalidated",
        RequestResult.Bypass => "bypass",
        _ => "error"
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _observeMethods, null);
        Volatile.Write(ref _observeHttp, null);
        Volatile.Write(ref _observeQueues, null);
        Source.Dispose();
    }
}

internal sealed class TelemetryOperation : IDisposable
{
    private readonly Histogram<double>? _duration;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Activity? _activity;
    private TagList _tags;
    private int _disposed;
    internal TelemetryOperation(Histogram<double>? duration, TimeProvider time, Activity? activity, TagList tags)
    {
        _duration = duration;
        _time = time;
        _started = time.GetTimestamp();
        _activity = activity;
        _tags = tags;
    }
    internal void Fail(Exception error)
    {
        var type = error.GetType().FullName ?? error.GetType().Name;
        _tags.Add("error.type", type);
        _activity?.SetTag("error.type", type).SetStatus(ActivityStatusCode.Error);
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _duration?.Record(_time.GetElapsedTime(_started).TotalSeconds, _tags);
        _activity?.Dispose();
    }
}
