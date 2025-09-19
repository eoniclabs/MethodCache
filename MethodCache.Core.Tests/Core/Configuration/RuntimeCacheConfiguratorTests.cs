using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Core.Configuration.Sources;
using Xunit;

namespace MethodCache.Core.Tests.Configuration
{
    public class RuntimeCacheConfiguratorTests
    {
        private interface IService
        {
            Task<string> GetAsync(int id);
        }

        private interface IExtendedService
        {
            Task<string> GetAsync(int id);
            Task<string> GetBySlugAsync(string slug);
        }

        [Fact]
        public async Task ApplyFluentAsync_OverridesExistingSettings()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            manager.AddSource(runtimeSource);

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            await configurator.ApplyFluentAsync(fluent =>
            {
                fluent.ForService<IService>()
                      .Method(s => s.GetAsync(default))
                      .Configure(options => options
                          .WithDuration(TimeSpan.FromMinutes(5))
                          .WithTags("runtime"))
                      .RequireIdempotent();
            });

            var settings = manager.GetMethodConfiguration(typeof(IService).FullName!, nameof(IService.GetAsync));

            Assert.NotNull(settings);
            Assert.Equal(TimeSpan.FromMinutes(5), settings!.Duration);
            Assert.Contains("runtime", settings.Tags);
            Assert.True(settings.IsIdempotent);
        }

        [Fact]
        public async Task ClearOverridesAsync_RemovesRuntimeSettings()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            manager.AddSource(runtimeSource);

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            await configurator.ApplyFluentAsync(fluent =>
            {
                fluent.ForService<IService>()
                      .Method(s => s.GetAsync(default))
                      .Configure(options => options.WithDuration(TimeSpan.FromMinutes(1)));
            });

            await configurator.ClearOverridesAsync();

            var settings = manager.GetMethodConfiguration(typeof(IService).FullName!, nameof(IService.GetAsync));
            Assert.Null(settings);
        }

        [Fact]
        public async Task GetOverridesAsync_ReturnsClonedOverrides()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            manager.AddSource(runtimeSource);

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            await configurator.ApplyFluentAsync(fluent =>
            {
                fluent.ForService<IService>()
                      .Method(s => s.GetAsync(default))
                      .Configure(options => options
                          .WithDuration(TimeSpan.FromMinutes(3))
                          .WithTags("runtime"));
            });

            var overrides = await configurator.GetOverridesAsync();

            Assert.Single(overrides);
            var settings = overrides[0].Settings;
            Assert.Equal(TimeSpan.FromMinutes(3), settings.Duration);
            Assert.Contains("runtime", settings.Tags);

            // Mutate the returned settings and ensure the stored overrides remain intact
            settings.Tags.Add("mutated");

            var overridesAfterMutation = await configurator.GetOverridesAsync();
            Assert.Single(overridesAfterMutation);
            Assert.DoesNotContain("mutated", overridesAfterMutation[0].Settings.Tags);
        }

        [Fact]
        public async Task RemoveOverrideAsync_RemovesSingleMethodOverride()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            manager.AddSource(runtimeSource);

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            await configurator.ApplyFluentAsync(fluent =>
            {
                fluent.ForService<IExtendedService>()
                      .Method(s => s.GetAsync(default))
                      .Configure(options => options.WithDuration(TimeSpan.FromMinutes(2)));

                fluent.ForService<IExtendedService>()
                      .Method(s => s.GetBySlugAsync(default!))
                      .Configure(options => options.WithDuration(TimeSpan.FromMinutes(7)));
            });

            var removed = await configurator.RemoveOverrideAsync(typeof(IExtendedService).FullName!, nameof(IExtendedService.GetAsync));
            Assert.True(removed);

            var overrides = await configurator.GetOverridesAsync();
            Assert.Single(overrides);
            Assert.Equal(nameof(IExtendedService.GetBySlugAsync), overrides[0].MethodName);

            Assert.Null(manager.GetMethodConfiguration(typeof(IExtendedService).FullName!, nameof(IExtendedService.GetAsync)));
            Assert.NotNull(manager.GetMethodConfiguration(typeof(IExtendedService).FullName!, nameof(IExtendedService.GetBySlugAsync)));

            var removedAgain = await configurator.RemoveOverrideAsync(typeof(IExtendedService).FullName!, nameof(IExtendedService.GetAsync));
            Assert.False(removedAgain);
        }

        [Fact]
        public async Task GetEffectiveConfigurationAsync_IncludesMergedSources()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            var baseSource = new StaticConfigurationSource();

            manager.AddSource(baseSource);
            manager.AddSource(runtimeSource);

            await manager.LoadConfigurationAsync();

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            await configurator.ApplyFluentAsync(fluent =>
            {
                fluent.ForService<IService>()
                      .Method(s => s.GetAsync(default))
                      .Configure(options => options.WithDuration(TimeSpan.FromMinutes(5)));
            });

            var entries = await configurator.GetEffectiveConfigurationAsync();

            Assert.Equal(2, entries.Count);

            var orderedKeys = entries.Select(e => e.MethodKey).ToArray();
            Assert.True(orderedKeys.SequenceEqual(orderedKeys.OrderBy(k => k, StringComparer.Ordinal)));

            var baseEntry = entries.Single(e => e.MethodKey == StaticConfigurationSource.BaseMethodKey);
            Assert.Equal(TimeSpan.FromMinutes(1), baseEntry.Settings.Duration);

            // Mutating the projection should not impact the manager's state
            baseEntry.Settings.Tags.Add("mutated");
            var stored = manager.GetMethodConfiguration(baseEntry.ServiceType, baseEntry.MethodName);
            Assert.NotNull(stored);
            Assert.DoesNotContain("mutated", stored!.Tags);
        }

        [Fact]
        public async Task ApplyOverridesAsync_AllowsDirectEntries()
        {
            var manager = new ConfigurationManager();
            var runtimeSource = new RuntimeOverrideConfigurationSource();
            manager.AddSource(runtimeSource);

            var configurator = new RuntimeCacheConfigurator(runtimeSource, manager);

            var overrides = new[]
            {
                new MethodCacheConfigEntry
                {
                    ServiceType = typeof(IService).FullName!,
                    MethodName = nameof(IService.GetAsync),
                    Settings = new CacheMethodSettings
                    {
                        Duration = TimeSpan.FromMinutes(4),
                        Tags = new List<string> { "direct" }
                    }
                }
            };

            await configurator.ApplyOverridesAsync(overrides);

            var settings = manager.GetMethodConfiguration(typeof(IService).FullName!, nameof(IService.GetAsync));
            Assert.NotNull(settings);
            Assert.Equal(TimeSpan.FromMinutes(4), settings!.Duration);
            Assert.Contains("direct", settings.Tags);
        }

        private sealed class StaticConfigurationSource : IConfigurationSource
        {
            internal static readonly string BaseMethodKey = $"{typeof(IService).FullName!.Replace('+', '.')}.StaticAsync";

            public int Priority => 10;
            public bool SupportsRuntimeUpdates => false;

            public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync()
            {
                var entry = new MethodCacheConfigEntry
                {
                    ServiceType = typeof(IService).FullName!,
                    MethodName = "StaticAsync",
                    Settings = new CacheMethodSettings
                    {
                        Duration = TimeSpan.FromMinutes(1),
                        Tags = new List<string> { "base" }
                    },
                    Priority = Priority
                };

                return Task.FromResult<IEnumerable<MethodCacheConfigEntry>>(new[] { entry });
            }
        }
    }
}
