using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OverloadCases.OverloadFirst { public sealed record Query(int Id); }
namespace OverloadCases.OverloadSecond { public sealed record Query(int Id); }

namespace MethodCache.Docs.Tests
{
    public class OverloadIdentityTests
    {
        [Fact]
        public async Task EquallyNamedArgumentTypes_KeepTheirOverloadsSeparate()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IQueries, Queries>();
            services.AddMethodCache();
            await using var provider = services.BuildServiceProvider();
            var queries = provider.GetRequiredService<IQueries>();

            Assert.Equal("first", queries.Find(new OverloadCases.OverloadFirst.Query(1)));
            Assert.Equal("second", queries.Find(new OverloadCases.OverloadSecond.Query(1)));
            Assert.Equal("first", queries.Find(new OverloadCases.OverloadFirst.Query(1)));
            Assert.Equal("second", queries.Find(new OverloadCases.OverloadSecond.Query(1)));
            var entries = provider.GetRequiredService<ICacheAdministrator>().ListEntries();
            Assert.Contains(entries, e => e.Key.Contains("Find(OverloadCases.OverloadFirst.Query)", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Key.Contains("Find(OverloadCases.OverloadSecond.Query)", StringComparison.Ordinal));
        }

        public interface IQueries
        {
            [Cache] string Find(OverloadCases.OverloadFirst.Query query);
            [Cache] string Find(OverloadCases.OverloadSecond.Query query);
        }

        private sealed class Queries : IQueries
        {
            public string Find(OverloadCases.OverloadFirst.Query query) => "first";
            public string Find(OverloadCases.OverloadSecond.Query query) => "second";
        }
    }
}
