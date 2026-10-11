using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class HttpWriteInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulPost_PreventsOlderGetPublishingIntoTheCurrentCache(bool distributed)
    {
        var backend = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        using var origin = new RacingOrigin();
        using var first = Provider(origin, distributed ? backend : null);
        using var second = distributed ? Provider(origin, backend) : null;
        using var reader = first.GetRequiredService<IHttpClientFactory>().CreateClient("race");
        using var writer = (second ?? first).GetRequiredService<IHttpClientFactory>().CreateClient("race");
        var pending = reader.GetAsync("https://api.example.com/race");
        await origin.Started.Task;
        using var write = await writer.PostAsync("https://api.example.com/race", new StringContent("update"));
        origin.Release.TrySetResult();
        using var old = await pending;
        using var current = await writer.GetAsync("https://api.example.com/race");
        Assert.Equal("new", await current.Content.ReadAsStringAsync());
    }

    private static ServiceProvider Provider(RacingOrigin origin, IDistributedCache? backend)
    {
        var services = new ServiceCollection();
        if (backend != null)
        {
            services.AddMethodCacheDistributedCache(_ => backend);
            services.Configure<HttpCacheStorageOptions>(options => options.UseDistributedCache = true);
        }
        services.AddHttpClient("race").ConfigurePrimaryHttpMessageHandler(() => origin).AddHttpCaching();
        return services.BuildServiceProvider();
    }

    private sealed class RacingOrigin : HttpMessageHandler
    {
        private int _reads;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var read = Interlocked.Increment(ref _reads);
            if (read == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(read == 1 ? "old" : "new"),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) } }
            };
        }
    }
}
