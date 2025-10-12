using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.Memory.Configuration;
using MethodCache.Providers.Memory.Extensions;
using MethodCache.Providers.Memory.Infrastructure;

namespace MethodCache.Providers.Memory.Tests;

public class MemoryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAdvancedMemoryStorage_ShouldRegisterServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAdvancedMemoryStorage();

        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<IMemoryStorage>().Should().BeOfType<AdvancedMemoryStorage>();
        serviceProvider.GetService<IStorageProvider>().Should().BeOfType<AdvancedMemoryStorageProvider>();
    }

    [Fact]
    public void AddAdvancedMemoryStorage_WithConfiguration_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAdvancedMemoryStorage(options =>
        {
            options.MaxEntries = 500;
            options.EvictionPolicy = EvictionPolicy.LFU;
        });

        var serviceProvider = services.BuildServiceProvider();
        var memoryStorage = serviceProvider.GetRequiredService<IMemoryStorage>();

        memoryStorage.Should().NotBeNull();
        memoryStorage.Should().BeOfType<AdvancedMemoryStorage>();
    }

    [Fact]
    public void AddAdvancedMemoryCache_ShouldRegisterServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAdvancedMemoryCache();

        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddAdvancedMemoryInfrastructure_ShouldRegisterInfrastructureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAdvancedMemoryInfrastructure();

        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<IMemoryStorage>().Should().NotBeNull();
        serviceProvider.GetService<IStorageProvider>().Should().NotBeNull();
        serviceProvider.GetService<ISerializer>().Should().NotBeNull();
    }

    [Fact]
    public void MultipleRegistrations_ShouldUseSingletonPattern()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAdvancedMemoryStorage();

        var serviceProvider = services.BuildServiceProvider();

        var storage1 = serviceProvider.GetRequiredService<IMemoryStorage>();
        var storage2 = serviceProvider.GetRequiredService<IMemoryStorage>();
        var provider1 = serviceProvider.GetRequiredService<IStorageProvider>();
        var provider2 = serviceProvider.GetRequiredService<IStorageProvider>();

        storage1.Should().BeSameAs(storage2);
        provider1.Should().BeSameAs(provider2);
    }
}