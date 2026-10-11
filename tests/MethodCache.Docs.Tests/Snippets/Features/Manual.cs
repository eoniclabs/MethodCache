using MethodCache;
using MethodCache.Extensibility;
using Microsoft.Extensions.Logging;

namespace Docs.Manual
{
    public sealed record Order(int Id, int CustomerId);

    public sealed record User(int Id);

    public interface IOrderRepository
    {
        Task<Order> GetAsync(int id, CancellationToken ct);

        Task<List<Order>> ListAsync(int customerId, string status, CancellationToken ct);

        Task<List<User>> GetUsersAsync(IReadOnlyList<int> ids, CancellationToken ct);
    }

    // begin-snippet: manual-basic
    public sealed class OrderReader(ICacheManager cache, IOrderRepository orders)
    {
        public ValueTask<Order> GetAsync(int id, CancellationToken ct = default) =>
            cache.GetOrCreateAsync(
                $"order:{id}",
                async (ctx, token) => await orders.GetAsync(id, token),
                o => o.WithDuration(TimeSpan.FromMinutes(30)).WithTags("orders", $"order:{id}"),
                cancellationToken: ct);
    }
    // end-snippet

    public static class Examples
    {
        public static async Task AllOptions(ICacheManager cache, IOrderRepository orders, ILogger logger, int customerId, string status)
        {
            // begin-snippet: manual-options
            var list = await cache.GetOrCreateAsync(
                $"orders:{customerId}:{status}",
                async (ctx, ct) => await orders.ListAsync(customerId, status, ct),
                o => o.WithDuration(TimeSpan.FromMinutes(30))
                      .WithSlidingExpiration(TimeSpan.FromMinutes(10))
                      .RefreshAhead(TimeSpan.FromMinutes(5))
                      .WithTags("orders", $"customer:{customerId}")
                      .WithVersion(2)
                      .OnHit(ctx => logger.LogDebug("Cache hit: {Key}", ctx.Key))
                      .OnMiss(ctx => logger.LogDebug("Cache miss: {Key}", ctx.Key))
                      .When(ctx => customerId > 0));   // skip the cache when false
            // end-snippet
        }

        public static async Task TryGet(ICacheManager cache)
        {
            // begin-snippet: manual-try-get
            var lookup = await cache.TryGetAsync<Order>("order:42");
            if (lookup.Found)
            {
                Console.WriteLine(lookup.Value);
            }
            // end-snippet
        }

        public static async Task Many(ICacheManager cache, IOrderRepository repository, int[] userIds)
        {
            // begin-snippet: manual-many
            var users = await cache.GetOrCreateManyAsync<User>(
                userIds.Select(id => $"user:{id}"),
                async (missingKeys, ctx, ct) =>
                {
                    var ids = missingKeys.Select(k => int.Parse(k["user:".Length..])).ToList();
                    var loaded = await repository.GetUsersAsync(ids, ct);   // one query for all misses
                    return loaded.ToDictionary(u => $"user:{u.Id}", u => u);
                },
                o => o.WithDuration(TimeSpan.FromHours(1)));
            // end-snippet
        }

        public static void Testing(IOrderRepository repository)
        {
            // begin-snippet: manual-testing
            ICacheManager cache = new NoOpCacheManager();   // never caches: the factory runs on every call
            var reader = new OrderReader(cache, repository);
            // end-snippet
        }
    }
}
