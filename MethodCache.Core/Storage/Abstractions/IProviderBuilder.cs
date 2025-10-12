using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Core.Storage.Abstractions;

/// <summary>
/// Base interface for all cache provider builders.
/// </summary>
public interface IProviderBuilder
{
    /// <summary>
    /// Registers the provider services with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    void Register(IServiceCollection services);

    /// <summary>
    /// Validates the provider configuration.
    /// </summary>
    void ValidateConfiguration();
}

/// <summary>
/// Interface for L1 (memory) cache providers.
/// </summary>
public interface IL1Provider : IProviderBuilder
{
    /// <summary>
    /// The provider name for identification.
    /// </summary>
    string Name { get; }
}

/// <summary>
/// Interface for L2 (distributed) cache providers.
/// </summary>
public interface IL2Provider : IProviderBuilder
{
    /// <summary>
    /// The provider name for identification.
    /// </summary>
    string Name { get; }
}

/// <summary>
/// Interface for L3 (persistent) cache providers.
/// </summary>
public interface IL3Provider : IProviderBuilder
{
    /// <summary>
    /// The provider name for identification.
    /// </summary>
    string Name { get; }
}