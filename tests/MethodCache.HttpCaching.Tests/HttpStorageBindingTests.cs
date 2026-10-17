using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpStorageBindingTests
{
    [Fact]
    public async Task OptionsChangeDuringOrigin_KeepsEachRequestsCapturedClientView()
    {
        var a = new HttpCacheOptions { AllowUnverifiedTransport = true }; a.Tags.Add("a");
        var b = new HttpCacheOptions { AllowUnverifiedTransport = true }; b.Tags.Add("b");
        var options = new SwitchingOptions(a);
        using var store = new TestHttpStore();
        using var origin = new Origin();
        using var client = new HttpClient(new HttpCacheHandler(new HttpCacheClientState("test"), store, options,
            TimeProvider.System, NullLogger<HttpCacheHandler>.Instance)
        { InnerHandler = origin });
        var first = client.GetStringAsync("https://example.test/slow");
        await origin.Started.Task;
        options.CurrentValue = b;
        Assert.Equal("/fast:2", await client.GetStringAsync("https://example.test/fast"));
        origin.Release.TrySetResult();
        Assert.Equal("/slow:1", await first);
        Assert.Single(store.Core.List(), info => info.Tags.Contains("a"));
        Assert.Single(store.Core.List(), info => info.Tags.Contains("b"));
        Assert.Equal("/slow:3", await client.GetStringAsync("https://example.test/slow"));
        Assert.Equal("/slow:3", await client.GetStringAsync("https://example.test/slow"));
        options.CurrentValue = a;
        Assert.Equal("/slow:1", await client.GetStringAsync("https://example.test/slow"));
        Assert.Equal(3, origin.Calls);
    }

    private sealed class SwitchingOptions(HttpCacheOptions initial) : IOptionsMonitor<HttpCacheOptions>
    {
        public HttpCacheOptions CurrentValue { get; set; } = initial;
        public HttpCacheOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HttpCacheOptions, string?> listener) => null;
    }

    private sealed class Origin : HttpMessageHandler
    {
        internal int Calls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath + ":" + call),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) } }
            };
        }
    }
}
