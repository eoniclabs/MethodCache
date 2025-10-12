using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.HttpCaching.Configuration;

public sealed class HttpCachingBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<HttpCacheOptions>> _optionActions = new();
    private readonly List<Action<HttpCacheStorageOptions>> _storageOptionActions = new();
    private Action<IServiceCollection>? _storageRegistration;

    public HttpCachingBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public HttpCachingBuilder Configure(Action<HttpCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _optionActions.Add(configure);
        return this;
    }

    public HttpCachingBuilder ConfigureStorage(Action<HttpCacheStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _storageOptionActions.Add(configure);
        return this;
    }

    public HttpCachingBuilder UseInMemoryStorage(Action<HttpCacheStorageOptions>? configure = null)
    {
        _storageRegistration = services =>
        {
            services.TryAddSingleton<IHttpCacheStorage, InMemoryHttpCacheStorage>();
        };

        if (configure != null)
        {
            _storageOptionActions.Add(configure);
        }

        return this;
    }

    public HttpCachingBuilder UseCustomStorage<TStorage>(Action<IServiceCollection>? registration = null)
        where TStorage : class, IHttpCacheStorage
    {
        _storageRegistration = services =>
        {
            if (registration != null)
            {
                registration(services);
            }
            else
            {
                services.TryAddSingleton<IHttpCacheStorage, TStorage>();
            }
        };

        return this;
    }

    internal void Apply()
    {
        _services.AddOptions<HttpCacheOptions>();
        _services.AddOptions<HttpCacheStorageOptions>();

        foreach (var configure in _optionActions)
        {
            _services.Configure(configure);
        }

        foreach (var configureStorage in _storageOptionActions)
        {
            _services.Configure(configureStorage);
        }

        if (_storageRegistration == null)
        {
            UseInMemoryStorage();
        }

        _storageRegistration?.Invoke(_services);
    }
}
