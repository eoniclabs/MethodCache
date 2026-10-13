using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

/// <summary>Edge cases from the third review of MethodCache.HttpCaching: registration through IHttpClientFactory.</summary>
public class HttpCacheEdgeCaseTests
{
    private sealed class CountingOrigin : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requests);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(count.ToString()) };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            return Task.FromResult(response);
        }
    }

    // ConfigureHttpClientDefaults keeps clients apart
    [Fact]
    public async Task AddHttpCaching_FromClientDefaults_KeepsEachClientsEntriesApart()
    {
        var origin = new CountingOrigin();
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b.AddHttpCaching(o => o.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => origin));
        services.AddHttpClient("a", c => c.DefaultRequestHeaders.Add("X-Api-Key", "tenant1"));
        services.AddHttpClient("b", c => c.DefaultRequestHeaders.Add("X-Api-Key", "tenant2"));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var a = await factory.CreateClient("a").GetStringAsync("https://api.example.com/x");
        var b = await factory.CreateClient("b").GetStringAsync("https://api.example.com/x");

        Assert.Equal("1", a);
        Assert.Equal("2", b); // client b must not get tenant 1's response
    }

    // A shared, already used, cookie-free handler
    [Fact]
    public async Task CreateClient_CachingClientSharesAnAlreadyUsedSocketsHandler_DoesNotThrow()
    {
        using var listener = new HttpListener();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes("ok");
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        var shared = new SocketsHttpHandler { UseCookies = false }; // already cookie-free, shared to share a connection pool
        var services = new ServiceCollection();
        services.AddHttpClient("plain").ConfigurePrimaryHttpMessageHandler(() => shared).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddHttpClient("cached").ConfigurePrimaryHttpMessageHandler(() => shared).SetHandlerLifetime(Timeout.InfiniteTimeSpan).AddHttpCaching();
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        Assert.Equal("ok", await factory.CreateClient("plain").GetStringAsync(prefix));
        await serve;

        var exception = Record.Exception(() => factory.CreateClient("cached"));

        Assert.Null(exception);
    }
}
