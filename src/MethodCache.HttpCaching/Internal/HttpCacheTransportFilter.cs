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
        }
        HttpMessageHandler? transport = builder.PrimaryHandler;
        while (transport is DelegatingHandler delegating) transport = delegating.InnerHandler;
        if (transport is SocketsHttpHandler sockets) sockets.UseCookies = false;
        else if (transport is HttpClientHandler client) client.UseCookies = false;
    };
}
