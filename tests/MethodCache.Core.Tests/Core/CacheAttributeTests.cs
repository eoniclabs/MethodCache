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
            var attribute = new CacheAttribute { Duration = "01:00:00", SlidingExpiration = "00:05:00", RefreshAhead = "00:01:00" };
            Assert.Equal("01:00:00", attribute.Duration);
            Assert.Equal("00:05:00", attribute.SlidingExpiration);
            Assert.Equal("00:01:00", attribute.RefreshAhead);
        }
    }
}
