using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Http;
using MethodCache.Core.Telemetry;
using MethodCache.Extensibility;
using MethodCache.HttpCaching.Internal;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Tests;

// Owns a real Core HTTP store and its container-scoped telemetry. The optional backend is a test-only,
// in-process native-capability implementation, never evidence of cross-process atomicity.
internal sealed class TestHttpStore : IHttpCacheStorage, IDisposable
{
    private readonly ServiceProvider _services;
    private readonly DistributedCacheLayer? _layer;
    private readonly HttpCacheStore _adapter;
    private readonly IHttpCacheStorage _default;
    internal HttpResponseStore Core { get; }
    internal DistributedCacheLayer Layer => _layer!;
    internal TestHttpStore(TimeProvider? time = null, IDistributedCache? distributed = null,
        long maximumBytes = 100 * 1024 * 1024, string prefix = "methodcache:", byte format = 2)
    {
        time ??= TimeProvider.System;
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var telemetry = new CoreTelemetry(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            Options.Create(new MethodCacheTelemetryOptions()), time);
        if (distributed != null)
            _layer = new DistributedCacheLayer(MethodCache.TestSupport.HttpEpochTestCache.Wrap(distributed), new JsonCacheSerializer(),
                new DistributedCacheOptions { KeyPrefix = prefix, ClockSkewTolerance = TimeSpan.Zero }, timeProvider: time, telemetry: telemetry);
        Core = new HttpResponseStore(Options.Create(new MethodCacheMemoryOptions { MaxHttpBytes = maximumBytes }), telemetry, time, _layer, format);
        _adapter = new HttpCacheStore(Core, time);
        _default = _adapter.ForClient("test", new HttpCacheOptions());
    }
    public IHttpCacheStorage ForClient(string name, HttpCacheOptions options) => _adapter.ForClient(name, options);
    public ValueTask<string> GetGenerationAsync(string key, CancellationToken token = default) => _default.GetGenerationAsync(key, token);
    public ValueTask InvalidateAsync(string key, CancellationToken token = default) => _default.InvalidateAsync(key, token);
    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken token = default) => _default.GetAsync(key, token);
    public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan ttl, CancellationToken token = default) => _default.SetAsync(key, entry, ttl, token);
    public ValueTask RemoveAsync(string key, CancellationToken token = default) => _default.RemoveAsync(key, token);
    public ValueTask ClearAsync(CancellationToken token = default) => _default.ClearAsync(token);
    public void Dispose() { Core.Dispose(); _layer?.Dispose(); _services.Dispose(); }
}

