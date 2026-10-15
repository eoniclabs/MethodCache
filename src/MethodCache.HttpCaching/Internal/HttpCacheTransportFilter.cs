using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;

namespace MethodCache.HttpCaching.Internal;

// Runs after the named client's complete builder configuration, including handlers configured after AddHttpCaching.
internal sealed class HttpCacheTransportFilter : IHttpMessageHandlerBuilderFilter
{
    /// <inheritdoc />
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
            if (!string.IsNullOrEmpty(builder.Name)) cache.UseClientName(builder.Name, builder.Services.GetRequiredService<HttpCacheClientStates>().For(builder.Name));
        }
        var options = builder.Services.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>().Get(builder.Name ?? string.Empty);
        if (options.KeepCookies) return;
        HttpMessageHandler? transport = builder.PrimaryHandler;
        while (transport is DelegatingHandler delegating) transport = delegating.InnerHandler;
        // Only when it changes anything: a handler shared between clients may already have sent requests
        var changed = false;
        if (transport is SocketsHttpHandler { UseCookies: true } sockets) { sockets.UseCookies = false; changed = true; }
        else if (transport is HttpClientHandler { UseCookies: true } client) { client.UseCookies = false; changed = true; }
        if (changed)
        {
            if (builder.AdditionalHandlers.OfType<HttpCacheHandler>().Any(cache => cache.ShouldWarnCookies()))
                builder.Services.GetRequiredService<ILogger<HttpCacheTransportFilter>>().LogWarning(
                    "HTTP caching disabled automatic transport cookies. Set KeepCookies to preserve cookie configuration and bypass response caching.");
        }
    };
}
