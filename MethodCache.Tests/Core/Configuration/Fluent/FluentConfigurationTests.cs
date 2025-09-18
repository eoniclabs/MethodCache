using System;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Options;
using Xunit;

namespace MethodCache.Tests.Core.Configuration.Fluent
{
    public class FluentConfigurationTests
    {
        private interface IUserService
        {
            string GetUser(int id);
            string GetAll();
        }

        [Fact]
        public void ApplyFluent_ConfiguresMethodSettings()
        {
            // Arrange
            var configuration = new MethodCacheConfiguration();

            configuration.ApplyFluent(fluent =>
            {
                fluent.DefaultPolicy(policy => policy.WithDuration(TimeSpan.FromMinutes(10)));

                fluent.ForService<IUserService>()
                    .Method(s => s.GetUser(default))
                    .Configure(options => options.WithDuration(TimeSpan.FromMinutes(1)).WithTags("users"))
                    .RequireIdempotent();

                fluent.ForService<IUserService>()
                    .Method(s => s.GetAll())
                    .Configure(options => options.WithTags("users", "list"));
            });

            var getUserSettings = configuration.GetMethodSettings(MethodKey(nameof(IUserService.GetUser)));
            var getAllSettings = configuration.GetMethodSettings(MethodKey(nameof(IUserService.GetAll)));

            // Assert default applied before overrides
            Assert.Equal(TimeSpan.FromMinutes(1), getUserSettings.Duration);
            Assert.Contains("users", getUserSettings.Tags);
            Assert.True(getUserSettings.IsIdempotent);

            // Method without explicit duration inherits default
            Assert.Equal(TimeSpan.FromMinutes(10), getAllSettings.Duration);
            Assert.Contains("users", getAllSettings.Tags);
            Assert.Contains("list", getAllSettings.Tags);
            Assert.False(getAllSettings.IsIdempotent);
        }

        [Fact]
        public void ApplyFluent_ConfiguresGroupSettings()
        {
            var configuration = new MethodCacheConfiguration();

            configuration.ApplyFluent(fluent =>
            {
                fluent.ForGroup("reports")
                      .Configure(options => options
                          .WithDuration(TimeSpan.FromMinutes(15))
                          .WithTags("analytics"));
            });

            var groupSettings = configuration.GetGroupSettings("reports");

            Assert.Equal(TimeSpan.FromMinutes(15), groupSettings.Duration);
            Assert.Contains("analytics", groupSettings.Tags);
        }

        [Fact]
        public void AddMethodCacheFluent_RegistersConfiguration()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

            services.AddMethodCacheFluent(fluent =>
            {
                fluent.ForService<IUserService>()
                      .Method(s => s.GetUser(default))
                      .Configure(options => options.WithTags("users"));
            });

            var provider = services.BuildServiceProvider();
            var configuration = (MethodCacheConfiguration)provider.GetRequiredService<IMethodCacheConfiguration>();
            var settings = configuration.GetMethodSettings(MethodKey(nameof(IUserService.GetUser)));

            Assert.Contains("users", settings.Tags);
        }

        private static string MethodKey(string methodName)
        {
            var fullName = typeof(IUserService).FullName!.Replace('+', '.');
            return $"{fullName}.{methodName}";
        }
    }
}
