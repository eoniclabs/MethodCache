using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using MethodCache.Core.Telemetry;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpOriginTelemetryCompletionTests
{
    [Theory]
    [InlineData("sync-success")]
    [InlineData("async-success")]
    [InlineData("sync-fault")]
    [InlineData("async-fault")]
    [InlineData("sync-throw")]
    [InlineData("async-cancel")]
    public async Task OriginCompletion_RecordsOneDurationAndRequest_WithErrorWhenNeeded(string completion)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var meter = new Meter(MethodCacheTelemetry.Name);
        var factory = Substitute.For<IMeterFactory>();
        factory.Create(Arg.Any<MeterOptions>()).Returns(meter);
        var durations = new ConcurrentQueue<(double Seconds, KeyValuePair<string, object?>[] Tags)>();
        var requests = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, self) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)) self.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "methodcache.load.duration") durations.Enqueue((value, tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "methodcache.requests") requests.Enqueue(value);
        });
        listener.Start();
        using var telemetry = new CoreTelemetry(factory, Options.Create(new MethodCacheTelemetryOptions()), time);
        var stopped = new ConcurrentQueue<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(activityListener);
        using var caller = new Activity("caller").Start();
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        var origin = new Origin(() =>
        {
            entered = true;
            Assert.Equal("MethodCache.load", Activity.Current?.OperationName);
            if (completion.StartsWith("async", StringComparison.Ordinal)) return delayed.Task;
            time.Advance(TimeSpan.FromSeconds(2));
            if (completion == "sync-throw") throw new InvalidOperationException("origin");
            return completion == "sync-fault"
                ? Task.FromException<HttpResponseMessage>(new InvalidOperationException("origin"))
                : Task.FromResult(Response());
        });
        using var storage = new TestHttpStore(time);
        using var client = new HttpClient(new HttpCacheHandler(new HttpCacheClientState("origin-probe"), storage,
            new TestOptionsMonitor<HttpCacheOptions>(new() { AllowUnverifiedTransport = true }), time,
            NullLogger<HttpCacheHandler>.Instance, telemetry)
        { InnerHandler = origin });

        var pending = client.GetAsync("https://example.test/resource");
        Assert.True(entered);
        if (completion.StartsWith("async", StringComparison.Ordinal))
        {
            Assert.False(pending.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(2));
            if (completion == "async-fault") delayed.SetException(new InvalidOperationException("origin"));
            else if (completion == "async-cancel") delayed.SetCanceled();
            else delayed.SetResult(Response());
        }
        var error = await Record.ExceptionAsync(async () => { using var response = await pending; });
        var success = completion.EndsWith("success", StringComparison.Ordinal);
        if (success) Assert.Null(error);
        else Assert.NotNull(error);
        Assert.Same(caller, Activity.Current);
        var loadActivity = Assert.Single(stopped);
        Assert.Equal(caller.SpanId, loadActivity.ParentSpanId);
        Assert.Equal(success ? ActivityStatusCode.Unset : ActivityStatusCode.Error, loadActivity.Status);
        var duration = Assert.Single(durations);
        Assert.Equal(2, duration.Seconds);
        Assert.Equal("http", Assert.Single(duration.Tags, tag => tag.Key == "methodcache.kind").Value);
        Assert.Equal("origin-probe", Assert.Single(duration.Tags, tag => tag.Key == "methodcache.http.client").Value);
        if (success) Assert.DoesNotContain(duration.Tags, tag => tag.Key == "error.type");
        else Assert.EndsWith(completion == "async-cancel" ? "TaskCanceledException" : "InvalidOperationException",
            (string)Assert.Single(duration.Tags, tag => tag.Key == "error.type").Value!);
        Assert.Equal(1, Assert.Single(requests));
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("body"),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };

    private sealed class Origin(Func<Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send();
    }
}
