using Xunit;

namespace MethodCache.Core.Tests.Core
{
    public class CacheAttributeTests
    {
        [Fact]
        public void CacheAttribute_DefaultConstructor_HasNoSettings()
        {
            var attribute = new CacheAttribute();
            Assert.Null(attribute.Group);
            Assert.Null(attribute.Duration);
            Assert.Null(attribute.SlidingExpiration);
            Assert.Null(attribute.RefreshAhead);
            Assert.Equal(0, attribute.Version);
        }

        [Fact]
        public void CacheAttribute_WithGroupName_GroupNameIsSet()
        {
            var attribute = new CacheAttribute { Group = "MyGroup" };
            Assert.Equal("MyGroup", attribute.Group);
        }

        [Fact]
        public void CacheAttribute_WithExpirationSettings_KeepsThem()
        {
            var attribute = new CacheAttribute { Duration = "1h", SlidingExpiration = "5m", RefreshAhead = "1m" };
            Assert.Equal("1h", attribute.Duration);
            Assert.Equal("5m", attribute.SlidingExpiration);
            Assert.Equal("1m", attribute.RefreshAhead);
        }
    }
}
