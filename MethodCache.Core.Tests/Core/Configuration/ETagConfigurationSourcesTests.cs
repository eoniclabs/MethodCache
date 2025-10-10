using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MethodCache.Abstractions.Registry;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Configuration.Sources;
using MethodCache.ETags.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Core.Tests.Configuration
{
    public class ETagConfigurationSourcesTests
    {
        private interface IAttributeBackedService
        {
            [Cache]
            [ETag(
                Strategy = MethodCache.ETags.Attributes.ETagGenerationStrategy.Version,
                IncludeParametersInETag = false,
                UseWeakETag = true,
                Metadata = new[] { "lang", "tenant" },
                ETagGeneratorType = typeof(FakeGenerator))]
            string GetValue(string id);
        }

        private sealed class FakeGenerator : IETagGenerator
        {
            public Task<string> GenerateETagAsync(object content, ETagGenerationContext context) => Task.FromResult("etag");
        }

        private sealed class AttributeBackedService : IAttributeBackedService
        {
            public string GetValue(string id) => $"Value for {id}";
        }

        [Fact]
        public async Task AttributeConfigurationSource_LoadsEtagMetadata()
        {
            var source = new AttributeConfigurationSource(new[] { typeof(IAttributeBackedService).Assembly });

            var entries = await source.LoadAsync();

            var entry = entries.Single(e => e.MethodName == nameof(IAttributeBackedService.GetValue));
            var metadata = entry.Settings.GetETagMetadata();

            Assert.NotNull(metadata);
            Assert.Equal("Version", metadata!.Strategy);
            Assert.False(metadata.IncludeParametersInETag);
            Assert.True(metadata.UseWeakETag);
            Assert.Null(metadata.CacheDuration); // No CacheDurationMinutes set in attribute
            Assert.Equal(typeof(FakeGenerator), metadata.ETagGeneratorType);
            Assert.Equal(new[] { "lang", "tenant" }, metadata.Metadata);
        }

        [Fact]
        public void MethodCacheRegistration_LoadsAttributeEtagMetadata()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddMethodCache(null, typeof(IAttributeBackedService).Assembly);

            using var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<IPolicyRegistry>();
            var policy = registry.GetPolicy($"{typeof(IAttributeBackedService).FullName}.{nameof(IAttributeBackedService.GetValue)}");
            var settings = CachePolicyConversion.ToCacheMethodSettings(policy.Policy);

            var metadata = settings.GetETagMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("Version", metadata!.Strategy);
            Assert.True(metadata.UseWeakETag);
        }

        [Fact]
        public async Task JsonConfigurationSource_LoadsEtagMetadata()
        {
            var generatorName = typeof(FakeGenerator).AssemblyQualifiedName;
            var json = $@"{{
  ""MethodCache"": {{
    ""Defaults"": {{
      ""ETag"": {{
        ""IncludeParametersInETag"": false,
        ""UseWeakETag"": true,
        ""Metadata"": [""global""]
      }}
    }},
    ""Services"": {{
      ""Test.Service.GetValue"": {{
        ""ETag"": {{
          ""Strategy"": ""LastModified"",
          ""CacheDuration"": ""00:10:00"",
          ""ETagGeneratorType"": ""{generatorName}""
        }}
      }}
    }}
  }}
}}";

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var source = new JsonConfigurationSource(configuration);

            var entries = await source.LoadAsync();
            var entry = entries.Single();

            var metadata = entry.Settings.GetETagMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("LastModified", metadata!.Strategy);
            Assert.False(metadata.IncludeParametersInETag);
            Assert.True(metadata.UseWeakETag);
            Assert.Equal(TimeSpan.FromMinutes(10), metadata.CacheDuration);
            Assert.Equal(typeof(FakeGenerator), metadata.ETagGeneratorType);
            Assert.Equal(new[] { "global" }, metadata.Metadata);
        }

        [Fact]
        public async Task YamlConfigurationSource_LoadsEtagMetadata()
        {
            var generatorName = typeof(FakeGenerator).AssemblyQualifiedName;
            var yaml = $@"defaults:
  eTag:
    useWeakETag: true
services:
  Test.Service.GetValue:
    eTag:
      strategy: Version
      metadata:
        - yaml
      cacheDuration: 00:05:00
      eTagGeneratorType: '{generatorName}'
";

            var path = Path.GetTempFileName();
            await File.WriteAllTextAsync(path, yaml);

            try
            {
                var source = new YamlConfigurationSource(path);
                var entries = await source.LoadAsync();
                var entry = entries.Single();

                var metadata = entry.Settings.GetETagMetadata();
                Assert.NotNull(metadata);
                Assert.Equal("Version", metadata!.Strategy);
                Assert.True(metadata.UseWeakETag);
                Assert.Equal(TimeSpan.FromMinutes(5), metadata.CacheDuration);
                Assert.Equal(typeof(FakeGenerator), metadata.ETagGeneratorType);
                Assert.Equal(new[] { "yaml" }, metadata.Metadata);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task OptionsMonitorPolicySource_MergesEtagMetadata()
        {
            var options = new MethodCacheOptions
            {
                ETag = new ETagOptions
                {
                    IncludeParametersInETag = true,
                    Metadata = new() { "global" }
                },
                Services =
                {
                    ["Test.Service"] = new ServiceCacheOptions
                    {
                        ETag = new ETagOptions
                        {
                            UseWeakETag = true
                        },
                        Methods =
                        {
                            ["GetValue"] = new MethodOptions
                            {
                                ETag = new ETagOptions
                                {
                                    Strategy = "Custom",
                                    CacheDuration = TimeSpan.FromMinutes(2),
                                    ETagGeneratorType = typeof(FakeGenerator).AssemblyQualifiedName
                                }
                            }
                        }
                    }
                }
            };

            var monitor = new StaticOptionsMonitor<MethodCacheOptions>(options);
            var source = new OptionsMonitorPolicySource(monitor);

            var snapshots = await source.GetSnapshotAsync();
            var snapshot = snapshots.Single();
            var settings = CachePolicyConversion.ToCacheMethodSettings(snapshot.Policy);

            var metadata = settings.GetETagMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("Custom", metadata!.Strategy);
            Assert.True(metadata.IncludeParametersInETag);
            Assert.True(metadata.UseWeakETag);
            Assert.Equal(TimeSpan.FromMinutes(2), metadata.CacheDuration);
            Assert.Equal(typeof(FakeGenerator), metadata.ETagGeneratorType);
        }

        private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        {
            public StaticOptionsMonitor(T current) => CurrentValue = current;

            public T CurrentValue { get; private set; }

            public T Get(string? name) => CurrentValue;

            public IDisposable OnChange(Action<T, string> listener) => Disposable.Instance;

            private sealed class Disposable : IDisposable
            {
                public static readonly Disposable Instance = new();
                public void Dispose() { }
            }
        }
    }
}
