using Xunit;
using MethodCache.Core;
using MethodCache.Core;

namespace MethodCache.Core.Tests.Core
{
    public class CacheAttributeTests
    {
        [Fact]
        public void CacheAttribute_DefaultConstructor_GroupNameIsNullAndRequireIdempotentIsFalse()
        {
            var attribute = new CacheAttribute();
            Assert.Null(attribute.GroupName);
            Assert.False(attribute.RequireIdempotent);
        }

        [Fact]
        public void CacheAttribute_WithGroupName_GroupNameIsSetAndRequireIdempotentIsFalse()
        {
            var attribute = new CacheAttribute("MyGroup");
            Assert.Equal("MyGroup", attribute.GroupName);
            Assert.False(attribute.RequireIdempotent);
        }

        [Fact]
        public void CacheAttribute_WithRequireIdempotent_RequireIdempotentIsTrue()
        {
            var attribute = new CacheAttribute { RequireIdempotent = true };
            Assert.Null(attribute.GroupName);
            Assert.True(attribute.RequireIdempotent);
        }

        [Fact]
        public void CacheAttribute_WithGroupNameAndRequireIdempotent_BothAreSet()
        {
            var attribute = new CacheAttribute("AnotherGroup") { RequireIdempotent = true };
            Assert.Equal("AnotherGroup", attribute.GroupName);
            Assert.True(attribute.RequireIdempotent);
        }
    }
}
