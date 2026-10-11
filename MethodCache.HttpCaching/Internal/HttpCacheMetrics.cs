using System.Diagnostics.Metrics;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// OpenTelemetry metrics on the <c>MethodCache</c> meter, so <c>AddMethodCacheInstrumentation()</c> (or
/// <c>AddMeter("MethodCache")</c>) collects them with the rest of MethodCache's metrics.
/// </summary>
internal sealed class HttpCacheMetrics : IDisposable
{
    public const string MeterName = "MethodCache";
    public const string RequestsName = "methodcache.http.requests_total";
    public const string DurationName = "methodcache.http.request_duration";

    private readonly Meter? _ownedMeter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;

    public HttpCacheMetrics(IMeterFactory? meterFactory = null)
    {
        var meter = meterFactory?.Create(MeterName) ?? (_ownedMeter = new Meter(MeterName));
        _requests = meter.CreateCounter<long>(RequestsName, "{request}",
            "HttpClient requests through the HTTP cache, by client and outcome (hit, miss, revalidated, stale, bypass)");
        _duration = meter.CreateHistogram<double>(DurationName, "ms",
            "Time to answer an HttpClient request through the HTTP cache, by client and outcome");
    }

    public void Record(string client, string outcome, double elapsedMilliseconds)
    {
        var clientTag = new KeyValuePair<string, object?>("client", client);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        _requests.Add(1, clientTag, outcomeTag);
        _duration.Record(elapsedMilliseconds, clientTag, outcomeTag);
    }

    public void Dispose() => _ownedMeter?.Dispose();
}
