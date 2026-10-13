using Microsoft.Extensions.Http;

namespace MethodCache.HttpCaching.Internal;

// Runs after the named client's complete builder configuration, including handlers configured after AddHttpCaching.
internal sealed class HttpCacheTransportFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (!builder.AdditionalHandlers.Any(handler => handler is HttpCacheHandler)) return;
        // Run the cache after every additional handler, so caller identity and request transforms are visible.
        foreach (var cache in builder.AdditionalHandlers.OfType<HttpCacheHandler>().ToArray())
        {
            builder.AdditionalHandlers.Remove(cache);
            builder.AdditionalHandlers.Add(cache);
            // Caching added for every client (ConfigureHttpClientDefaults) keeps each client's entries apart
            if (!string.IsNullOrEmpty(builder.Name)) cache.UseClientName(builder.Name);
        }
        HttpMessageHandler? transport = builder.PrimaryHandler;
        while (transport is DelegatingHandler delegating) transport = delegating.InnerHandler;
        // Only when it changes anything: a handler shared between clients may already have sent requests
        if (transport is SocketsHttpHandler { UseCookies: true } sockets) sockets.UseCookies = false;
        else if (transport is HttpClientHandler { UseCookies: true } client) client.UseCookies = false;
    };
}
