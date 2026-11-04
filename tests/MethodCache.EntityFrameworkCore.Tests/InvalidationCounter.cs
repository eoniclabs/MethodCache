using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore.Tests;

/// <summary>Counts the tag invalidations one host's cache makes, from its own metrics.</summary>
public sealed class InvalidationCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private int _calls;

    public InvalidationCounter(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "methodcache.invalidations" && ReferenceEquals(instrument.Meter.Scope, factory)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tag = false;
            foreach (var pair in tags) tag |= pair is { Key: "methodcache.invalidation.type", Value: "tag" };
            if (tag) Interlocked.Increment(ref _calls);
        });
        _listener.Start();
    }

    public int Calls => Volatile.Read(ref _calls);

    public void Dispose() => _listener.Dispose();
}
