# Redis integration tests

These tests run two MethodCache instances against one real Redis and check that values and invalidations reach both.

They use the Redis in `METHODCACHE_REDIS_URL` if it is set, otherwise they start one in Docker with Testcontainers. Without either they are skipped, unless `METHODCACHE_REQUIRE_INFRA_TESTS=true`, which makes a missing Redis fail the run.

```bash
# Against a local Redis
METHODCACHE_REDIS_URL=localhost:6379 dotnet test MethodCache.Providers.Redis.IntegrationTests

# With Docker running
dotnet test MethodCache.Providers.Redis.IntegrationTests
```
