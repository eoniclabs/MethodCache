using Microsoft.Extensions.Http;

namespace MethodCache.HttpCaching.Internal;

// Runs after the named client's complete builder configuration, including handlers configured after AddHttpCaching.
internal sealed class HttpCacheTransportFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (!builder.AdditionalHandlers.Any(handler => handler is HttpCacheHandler)) return;
        HttpMessageHandler? transport = builder.PrimaryHandler;
        while (transport is DelegatingHandler delegating) transport = delegating.InnerHandler;
        if (transport is SocketsHttpHandler sockets) sockets.UseCookies = false;
        else if (transport is HttpClientHandler client) client.UseCookies = false;
    };
}
