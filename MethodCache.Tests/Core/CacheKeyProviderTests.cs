using Xunit;
using MethodCache.Core;

namespace MethodCache.Tests
{
    public class CacheKeyProviderTests
    {
        private class TestUser : ICacheKeyProvider
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string CacheKeyPart => $"user-{Id}-{Name}";
        }

        [Fact]
        public void CacheKeyProvider_ReturnsCorrectKeyPart()
        {
            var user = new TestUser { Id = 1, Name = "Test" };
            Assert.Equal("user-1-Test", user.CacheKeyPart);
        }
    }
}