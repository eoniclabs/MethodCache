using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class TransportCookieIsolationTests
{
    [Theory]
    [InlineData("sockets")]
    [InlineData("httpclient")]
    [InlineData("wrapped")]
    public async Task AutomaticCookies_ChangingSession_NeverReusesAnotherSessionsResponse(string kind)
    {
        await using var origin = new CookieOrigin();
        var jar = new CookieContainer();
        jar.Add(origin.Uri, new Cookie("session", "A"));
        using HttpMessageHandler transport = kind switch
        {
            "httpclient" => new HttpClientHandler { CookieContainer = jar, UseCookies = true },
            "wrapped" => new ForwardHandler(new SocketsHttpHandler { CookieContainer = jar, UseCookies = true }),
            _ => new SocketsHttpHandler { CookieContainer = jar, UseCookies = true }
        };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), transport));

        Assert.Equal("session=A", await client.GetStringAsync(origin.Uri));
        jar.Add(origin.Uri, new Cookie("session", "B"));
        Assert.Equal("session=B", await client.GetStringAsync(origin.Uri));
        Assert.Equal(2, origin.Requests);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task TransportCookiePolicy_EmptyJar_BypassesOnlyWhenCookiesAreEnabled(bool useCookies, int expectedRequests)
    {
        await using var origin = new CookieOrigin();
        using var transport = new SocketsHttpHandler { UseCookies = useCookies };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), transport));
        Assert.Equal("anonymous", await client.GetStringAsync(origin.Uri));
        Assert.Equal("anonymous", await client.GetStringAsync(origin.Uri));
        Assert.Equal(expectedRequests, origin.Requests);
    }

    [Fact]
    public async Task AutomaticCookies_OnlyIfCached_DoesNotContactOrigin()
    {
        await using var origin = new CookieOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new SocketsHttpHandler()));
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Uri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { OnlyIfCached = true };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, origin.Requests);
    }

    [Fact]
    public async Task HiddenCookieTransport_DefaultPolicy_NeverCachesAnotherSessionsResponse()
    {
        await using var origin = new CookieOrigin();
        var jar = new CookieContainer();
        jar.Add(origin.Uri, new Cookie("session", "A"));
        using var transport = new HiddenHandler(new SocketsHttpHandler { CookieContainer = jar });
        var state = new MethodCache.HttpCaching.Internal.HttpCacheClientState("hidden");
        using var handler = new MethodCache.HttpCaching.Internal.HttpCacheHandler(state,
            HttpCacheTestFactory.CreateInMemoryStorage(), new TestOptionsMonitor<HttpCacheOptions>(new()), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MethodCache.HttpCaching.Internal.HttpCacheHandler>.Instance) { InnerHandler = transport };
        using var client = new HttpClient(handler);
        Assert.Equal("session=A", await client.GetStringAsync(origin.Uri));
        jar.Add(origin.Uri, new Cookie("session", "B"));
        Assert.Equal("session=B", await client.GetStringAsync(origin.Uri));
        Assert.Equal(2, origin.Requests);
    }

    private sealed class HiddenHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _transport = new(inner);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _transport.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) _transport.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ForwardHandler : DelegatingHandler
    {
        public ForwardHandler(HttpMessageHandler inner) { InnerHandler = inner; }
    }

    private sealed class CookieOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private int _requests;
        public Uri Uri { get; }
        public int Requests => Volatile.Read(ref _requests);
        public CookieOrigin()
        {
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/me");
            _server = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var identity = "anonymous";
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    {
                        if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) identity = line[7..].Trim();
                    }
                    Interlocked.Increment(ref _requests);
                    var body = Encoding.ASCII.GetBytes(identity);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nCache-Control: private, max-age=600\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _server;
            _stop.Dispose();
        }
    }
}
