using System;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.ETags.Attributes;
using MethodCache.ETags.Configuration;
using Xunit;

namespace MethodCache.ETags.Tests.Configuration
{
    public class CacheMethodSettingsExtensionsTests
    {
        [Fact]
        public void GetETagSettings_ReturnsNull_WhenMetadataMissing()
        {
            var settings = new CacheMethodSettings();

            var result = settings.GetETagSettings();

            Assert.Null(result);
        }

        [Fact]
        public void GetETagSettings_ConvertsMetadataToSettings()
        {
            var metadata = new ETagMetadata
            {
                Strategy = nameof(ETagGenerationStrategy.Custom),
                IncludeParametersInETag = false,
                UseWeakETag = true,
                CacheDuration = TimeSpan.FromMinutes(3),
                Metadata = new[] { "user" },
                ETagGeneratorType = typeof(SampleGenerator)
            };

            var settings = new CacheMethodSettings();
            settings.SetETagMetadata(metadata);

            var result = settings.GetETagSettings();

            Assert.NotNull(result);
            Assert.Equal(ETagGenerationStrategy.Custom, result!.Strategy);
            Assert.False(result.IncludeParametersInETag);
            Assert.True(result.UseWeakETag);
            Assert.Equal(TimeSpan.FromMinutes(3), result.CacheDuration);
            Assert.Equal(new[] { "user" }, result.Metadata);
            Assert.Equal(typeof(SampleGenerator), result.ETagGeneratorType);
        }

        private sealed class SampleGenerator : IETagGenerator
        {
            public Task<string> GenerateETagAsync(object content, ETagGenerationContext context) => Task.FromResult("etag");
        }
    }
}
