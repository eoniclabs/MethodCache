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

/// <summary>
/// The attribute names on MethodCache's metrics and spans. They are part of the public contract: dashboards and
/// alerts are built on them. All live under <c>methodcache.</c>, so they never collide with the application's or the
/// collector's own attributes, or with a backend's built-in span fields such as <c>name</c> and <c>kind</c>.
/// </summary>
internal static class TelemetryAttributes
{
    /// <summary>What was cached: <c>method</c> (a generated method), <c>manual</c> (an <see cref="ICacheManager"/>
    /// call by key) or <c>http</c> (an HttpClient response).</summary>
    internal const string Kind = "methodcache.kind";
    /// <summary>The method id of a generated method.</summary>
    internal const string Method = "methodcache.method";
    /// <summary>The HttpClient name of an HTTP request.</summary>
    internal const string HttpClient = "methodcache.http.client";
    /// <summary><c>true</c> on a <c>GetOrCreateManyAsync</c> batch, which reports one outcome for all its keys.</summary>
    internal const string Bulk = "methodcache.bulk";
    /// <summary>hit, miss, stale, revalidated, bypass or error.</summary>
    internal const string Result = "methodcache.result";
    /// <summary>key, tag, method or clear.</summary>
    internal const string InvalidationType = "methodcache.invalidation.type";
    /// <summary>Where an invalidation, or a queued one, is: local, distributed, queued, storage, publication and so on.</summary>
    internal const string InvalidationStage = "methodcache.invalidation.stage";
    /// <summary>The distributed cache operation: get, set, remove or invalidate.</summary>
    internal const string Operation = "methodcache.operation";
    /// <summary>The OpenTelemetry semantic-convention attribute for a failed operation's exception type.</summary>
    internal const string ErrorType = "error.type";
}

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
    private readonly AsyncLocal<CustomRequest?> _customRequest = new();
    private class CustomRequest { internal bool OwnedByMemory; }
    private sealed class CustomRequest<T>(CoreTelemetry telemetry, string key, CacheRuntimePolicy policy, Func<Task<T>> factory) : CustomRequest
    {
        internal bool Ran;
        internal Task<T> Invoke()
        {
            Ran = true;
            return OwnedByMemory ? factory() : telemetry.OriginAsync(policy.MethodId, key, policy.Tags, factory);
        }
    }
    internal void ClaimCustomRequest()
    {
        if (_customRequest.Value is { } request) request.OwnedByMemory = true;
    }
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
        _loads = meter.CreateHistogram<double>("methodcache.load.duration", "s", description: null, tags: null, SecondsAdvice);
        _invalidations = meter.CreateCounter<long>("methodcache.invalidations", "{invalidation}");
        _distributed = meter.CreateHistogram<double>("methodcache.distributed.duration", "s", description: null, tags: null, SecondsAdvice);
        meter.CreateObservableUpDownCounter("methodcache.entries", ObserveEntries, "{entry}");
        meter.CreateObservableUpDownCounter("methodcache.http.size", () => Observe().LocalHttpBytes, "By");
        meter.CreateObservableUpDownCounter("methodcache.distributed.queue", ObserveQueues, "{invalidation}");
    }

    // Without advice an SDK falls back to its default buckets (0, 5, 10 ... 10000), made for milliseconds: every
    // duration in seconds would land in the first one. These are the ones ASP.NET Core advises for
    // http.server.request.duration.
    private static readonly InstrumentAdvice<double> SecondsAdvice = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    // ICacheManager calls by key reach telemetry with these names in place of a method id. They are not methods, so
    // they are reported as kind=manual, the bulk one also as bulk=true, and with no method attribute.
    internal const string ManualName = CacheKeyLookup.MethodName;
    internal const string ManualBulkName = CacheKeyLookup.MethodName + ".Bulk";
    private static readonly object BoxedTrue = true;
    private static readonly KeyValuePair<string, object?> KindMethod = new(TelemetryAttributes.Kind, "method");
    private static readonly KeyValuePair<string, object?> KindManual = new(TelemetryAttributes.Kind, "manual");
    private static readonly KeyValuePair<string, object?> KindHttp = new(TelemetryAttributes.Kind, "http");
    private static readonly KeyValuePair<string, object?> ManualBulk = new(TelemetryAttributes.Bulk, BoxedTrue);

    /// <summary>
    /// The kind attribute and the attribute that names what was cached: the method, the HTTP client, the bulk marker
    /// for a manual batch, or none (<c>Key == null</c>) for a manual call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (KeyValuePair<string, object?> Kind, KeyValuePair<string, object?> Subject) Describe(string kind, string name)
    {
        if (kind == "http") return (KindHttp, new(TelemetryAttributes.HttpClient, name));
        if (name.StartsWith(ManualName, StringComparison.Ordinal))
        {
            if (name.Length == ManualName.Length) return (KindManual, default);
            if (string.Equals(name, ManualBulkName, StringComparison.Ordinal)) return (KindManual, ManualBulk);
        }
        return (KindMethod, new(TelemetryAttributes.Method, name));
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
        var outcome = RequestResult.Error;
        var previous = _customRequest.Value;
        var request = new CustomRequest<T>(this, key, policy, factory);
        _customRequest.Value = request;
        try
        {
            var value = await manager.GetOrCreateFastAsync(key, policy.MethodId, request.Invoke, policy, token).ConfigureAwait(false);
            outcome = manager is MethodCache.NoOpCacheManager ? RequestResult.Bypass
                : request.Ran ? RequestResult.Miss : RequestResult.Hit;
            return value;
        }
        finally
        {
            _customRequest.Value = previous;
            if (!request.OwnedByMemory) Request("method", policy.MethodId, outcome);
        }
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
        yield return new Measurement<long>(current.LocalMethodEntries, KindMethod);
        yield return new Measurement<long>(current.LocalHttpEntries, KindHttp);
    }

    private IEnumerable<Measurement<long>> ObserveQueues()
    {
        var queues = Volatile.Read(ref _disposed) == 0 ? Volatile.Read(ref _observeQueues)?.Invoke() ?? default : default;
        yield return new Measurement<long>(queues?.Distributed ?? 0, new KeyValuePair<string, object?>(TelemetryAttributes.InvalidationStage, "storage"));
        yield return new Measurement<long>(queues?.Publication ?? 0, new KeyValuePair<string, object?>(TelemetryAttributes.InvalidationStage, "publication"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Request(string kind, string name, RequestResult result)
    {
        if (!_requests.Enabled) return;
        var (kindTag, subject) = Describe(kind, name);
        var resultTag = new KeyValuePair<string, object?>(TelemetryAttributes.Result, ResultName(result));
        if (subject.Key == null) _requests.Add(1, kindTag, resultTag);
        else _requests.Add(1, kindTag, subject, resultTag);
    }

    internal void Invalidations(string type, string stage, long targets)
    {
        if (targets != 0 && _invalidations.Enabled)
            _invalidations.Add(targets, new(TelemetryAttributes.InvalidationType, type), new(TelemetryAttributes.InvalidationStage, stage));
    }

    internal TelemetryOperation? BeginLoad(string kind, string name, string? key = null, IReadOnlyCollection<string>? tags = null)
    {
        if (!_loads.Enabled && !Source.HasListeners()) return null;
        var (kindTag, subject) = Describe(kind, name);
        var activity = Source.StartActivity("MethodCache.load", ActivityKind.Internal);
        activity?.SetTag(kindTag.Key, kindTag.Value);
        if (subject.Key != null) activity?.SetTag(subject.Key, subject.Value);
        RecordIdentities(activity, key, tags);
        return new TelemetryOperation(_loads.Enabled ? _loads : null, _time, activity, kindTag, subject);
    }

    internal TelemetryOperation? BeginDistributed(string operation)
    {
        if (!_distributed.Enabled && !Source.HasListeners()) return null;
        // Internal, not Client: the Redis and SqlClient instrumentations add the client spans (with db.* attributes)
        // under it, one for each command it sends
        var activity = Source.StartActivity("MethodCache.distributed", ActivityKind.Internal);
        activity?.SetTag(TelemetryAttributes.Operation, operation);
        return new TelemetryOperation(_distributed.Enabled ? _distributed : null, _time, activity,
            new(TelemetryAttributes.Operation, operation));
    }

    internal Activity? BeginInvalidation(string type, string[]? keys = null, string[]? tags = null)
    {
        if (!Source.HasListeners()) return null;
        var activity = Source.StartActivity("MethodCache.invalidate", ActivityKind.Internal);
        activity?.SetTag(TelemetryAttributes.InvalidationType, type);
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
    private readonly KeyValuePair<string, object?> _first;
    private readonly KeyValuePair<string, object?> _second;
    private string? _errorType;
    private int _disposed;
    internal TelemetryOperation(Histogram<double>? duration, TimeProvider time, Activity? activity,
        KeyValuePair<string, object?> first, KeyValuePair<string, object?> second = default)
    {
        _duration = duration;
        _time = time;
        _started = time.GetTimestamp();
        _activity = activity;
        _first = first;
        _second = second;
    }
    internal void Fail(Exception error)
    {
        var type = error.GetType().FullName ?? error.GetType().Name;
        _errorType = type;
        _activity?.SetTag(TelemetryAttributes.ErrorType, type).SetStatus(ActivityStatusCode.Error);
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_duration != null)
        {
            var seconds = _time.GetElapsedTime(_started).TotalSeconds;
            // Operations carry one or two fixed tags. Record the optional error tag on the
            // stack rather than retaining TagList's eight inline slots in every scope.
            if (_second.Key == null)
            {
                if (_errorType == null) _duration.Record(seconds, _first);
                else _duration.Record(seconds, _first, new(TelemetryAttributes.ErrorType, _errorType));
            }
            else
            {
                if (_errorType == null) _duration.Record(seconds, _first, _second);
                else _duration.Record(seconds, _first, _second, new(TelemetryAttributes.ErrorType, _errorType));
            }
        }
        _activity?.Dispose();
    }
}
