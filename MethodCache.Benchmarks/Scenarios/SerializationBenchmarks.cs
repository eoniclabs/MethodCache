using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Benchmarks.Core;
using MethodCache.Core;
using MethodCache.Core.Runtime;
using MethodCache.Abstractions.Registry;
using MethodCache.Benchmarks.Infrastructure;
using System.Text.Json;
using System.Text;
using MessagePack;
using MethodCache.Core;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Benchmarks.Scenarios;

/// <summary>
/// Benchmarks comparing different serialization methods for cache keys
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[RankColumn]
public class SerializationBenchmarks : BenchmarkBase
{
    private ISerializationTestService _messagePackService = null!;
    private ISerializationTestService _jsonService = null!;
    private ISerializationTestService _toStringService = null!;

    [Params(10, 100, 1000)]
    public int ObjectCount { get; set; }

    [Params("Small", "Medium", "Large")]
    public string ObjectType { get; set; } = "Small";

    protected override void OnSetupComplete()
    {
        // Create services with different key generators
        _messagePackService = CreateServiceWithKeyGenerator<MessagePackKeyGenerator>();
        _jsonService = CreateServiceWithKeyGenerator<JsonKeyGenerator>();
        _toStringService = CreateServiceWithKeyGenerator<ToStringKeyGenerator>();
    }

    private ISerializationTestService CreateServiceWithKeyGenerator<TKeyGenerator>()
        where TKeyGenerator : class, ICacheKeyGenerator
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        
        // Replace key generator
        services.Remove(services.First(s => s.ServiceType == typeof(ICacheKeyGenerator)));
        services.AddSingleton<ICacheKeyGenerator, TKeyGenerator>();
        
        services.AddSingleton<ISerializationTestService, SerializationTestService>();
        
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISerializationTestService>();
    }

    [Benchmark(Baseline = true)]
    public async Task MessagePack_Serialization()
    {
        await RunSerializationTest(_messagePackService);
    }

    [Benchmark]
    public async Task Json_Serialization()
    {
        await RunSerializationTest(_jsonService);
    }

    [Benchmark]
    public async Task ToString_Serialization()
    {
        await RunSerializationTest(_toStringService);
    }

    [Benchmark]
    public async Task MessagePack_ComplexObjects()
    {
        await RunComplexObjectTest(_messagePackService);
    }

    [Benchmark]
    public async Task Json_ComplexObjects()
    {
        await RunComplexObjectTest(_jsonService);
    }

    [Benchmark]
    public async Task ToString_ComplexObjects()
    {
        await RunComplexObjectTest(_toStringService);
    }

    [Benchmark]
    public async Task MessagePack_CacheKeyCollisions()
    {
        await RunCollisionTest(_messagePackService);
    }

    [Benchmark]
    public async Task Json_CacheKeyCollisions()
    {
        await RunCollisionTest(_jsonService);
    }

    [Benchmark]
    public async Task ToString_CacheKeyCollisions()
    {
        await RunCollisionTest(_toStringService);
    }

    private async Task RunSerializationTest(ISerializationTestService service)
    {
        for (int i = 0; i < ObjectCount; i++)
        {
            await service.GetSimpleDataAsync(i, ObjectType);
        }
    }

    private async Task RunComplexObjectTest(ISerializationTestService service)
    {
        var complexParams = CreateComplexParameters();
        
        for (int i = 0; i < ObjectCount / 10; i++)
        {
            await service.GetComplexDataAsync(complexParams, i, ObjectType);
        }
    }

    private async Task RunCollisionTest(ISerializationTestService service)
    {
        // Test scenarios that might cause key collisions
        var tasks = new List<Task>();
        
        for (int i = 0; i < ObjectCount; i++)
        {
            // Similar but different parameters
            tasks.Add(service.GetDataWithArrayAsync(new[] { i, i + 1 }));
            tasks.Add(service.GetDataWithArrayAsync(new[] { i + 1, i }));
            
            // Similar strings
            tasks.Add(service.GetDataWithStringAsync($"test_{i}"));
            tasks.Add(service.GetDataWithStringAsync($"test{i}"));
        }
        
        await Task.WhenAll(tasks);
    }

    private ComplexParameter CreateComplexParameters()
    {
        return new ComplexParameter
        {
            Id = Random.Shared.Next(1, 1000),
            Name = $"Complex_{Random.Shared.Next()}",
            Values = Enumerable.Range(0, 10).Select(i => Random.Shared.NextDouble()).ToArray(),
            Metadata = new Dictionary<string, object>
            {
                ["key1"] = "value1",
                ["key2"] = 42,
                ["key3"] = true
            },
            NestedObject = new NestedParameter
            {
                Type = "nested",
                Data = Enumerable.Range(0, 5).ToDictionary(i => $"nested_{i}", i => $"data_{i}")
            }
        };
    }
}

public interface ISerializationTestService
{
    Task<object> GetSimpleDataAsync(int id, string type);
    Task<object> GetComplexDataAsync(ComplexParameter param, int id, string type);
    Task<object> GetDataWithArrayAsync(int[] values);
    Task<object> GetDataWithStringAsync(string text);
}

public class SerializationTestService : ISerializationTestService
{
    private readonly ICacheManager _cacheManager;
    private readonly IPolicyRegistry _policyRegistry;
    private readonly ICacheKeyGenerator _keyGenerator;

    public SerializationTestService(
        ICacheManager cacheManager,
        IPolicyRegistry policyRegistry,
        ICacheKeyGenerator keyGenerator)
    {
        _cacheManager = cacheManager;
        _policyRegistry = policyRegistry;
        _keyGenerator = keyGenerator;
    }

    [Cache(Duration = "00:05:00")]
    public virtual async Task<object> GetSimpleDataAsync(int id, string type)
    {
        var settings = _policyRegistry.GetSettingsFor<SerializationTestService>(nameof(GetSimpleDataAsync));
        var args = new object[] { id, type };

        return await _cacheManager.GetOrCreateAsync<object>(
            "GetSimpleDataAsync",
            args,
            async () => await CreateSimpleDataAsync(id, type),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:05:00")]
    public virtual async Task<object> GetComplexDataAsync(ComplexParameter param, int id, string type)
    {
        var settings = _policyRegistry.GetSettingsFor<SerializationTestService>(nameof(GetComplexDataAsync));
        var args = new object[] { param, id, type };

        return await _cacheManager.GetOrCreateAsync<object>(
            "GetComplexDataAsync",
            args,
            async () => await CreateComplexDataAsync(param, id, type),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:05:00")]
    public virtual async Task<object> GetDataWithArrayAsync(int[] values)
    {
        var settings = _policyRegistry.GetSettingsFor<SerializationTestService>(nameof(GetDataWithArrayAsync));
        var args = new object[] { values };

        return await _cacheManager.GetOrCreateAsync<object>(
            "GetDataWithArrayAsync",
            args,
            async () => await CreateArrayDataAsync(values),
            settings,
            _keyGenerator);
    }

    [Cache(Duration = "00:05:00")]
    public virtual async Task<object> GetDataWithStringAsync(string text)
    {
        var settings = _policyRegistry.GetSettingsFor<SerializationTestService>(nameof(GetDataWithStringAsync));
        var args = new object[] { text };

        return await _cacheManager.GetOrCreateAsync<object>(
            "GetDataWithStringAsync",
            args,
            async () => await CreateStringDataAsync(text),
            settings,
            _keyGenerator);
    }

    private async Task<object> CreateSimpleDataAsync(int id, string type)
    {
        await Task.Yield();
        return type switch
        {
            "Small" => SmallModel.Create(id),
            "Medium" => MediumModel.Create(id),
            "Large" => LargeModel.Create(id),
            _ => new { Id = id, Type = type }
        };
    }

    private async Task<object> CreateComplexDataAsync(ComplexParameter param, int id, string type)
    {
        await Task.Delay(1);
        return new { Parameter = param, Id = id, Type = type, Created = DateTime.UtcNow };
    }

    private async Task<object> CreateArrayDataAsync(int[] values)
    {
        await Task.Yield();
        return new { Values = values, Sum = values.Sum(), Count = values.Length };
    }

    private async Task<object> CreateStringDataAsync(string text)
    {
        await Task.Yield();
        return new { Text = text, Length = text.Length, Hash = text.GetHashCode() };
    }
}

// Custom key generators for comparison
public class JsonKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (policy.Version.HasValue)
            keyBuilder.Append($"_v{policy.Version.Value}");

        foreach (var arg in args)
        {
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                var json = JsonSerializer.Serialize(arg);
                keyBuilder.Append($"_{json}");
            }
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
        var base64Hash = Convert.ToBase64String(hash);

        if (policy.Version.HasValue)
            return $"{base64Hash}_v{policy.Version.Value}";
        return base64Hash;
    }
}

public class ToStringKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (policy.Version.HasValue)
            keyBuilder.Append($"_v{policy.Version.Value}");

        foreach (var arg in args)
        {
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                keyBuilder.Append($"_{arg?.ToString() ?? "null"}");
            }
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
        var base64Hash = Convert.ToBase64String(hash);

        if (policy.Version.HasValue)
            return $"{base64Hash}_v{policy.Version.Value}";
        return base64Hash;
    }
}

// Test parameter classes
public class ComplexParameter
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double[] Values { get; set; } = Array.Empty<double>();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public NestedParameter NestedObject { get; set; } = new();
}

public class NestedParameter
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new();
}
