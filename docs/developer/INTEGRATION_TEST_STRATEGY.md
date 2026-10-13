# Integration Tests

The Redis and SQL Server providers each have an integration test project that runs two MethodCache instances against one real server. The tests check that a value one instance stores is read by the other, and that key and tag invalidations reach the other instance's in-memory cache through the backplane.

`MethodCache.HttpCaching.IntegrationTests` needs no server: it runs the HTTP cache handler in process.

## Where the server comes from

| Project | Environment variable | Fallback |
|---------|----------------------|----------|
| `MethodCache.Redis.IntegrationTests` | `METHODCACHE_REDIS_URL` | `redis:7-alpine` container |
| `MethodCache.SqlServer.IntegrationTests` | `METHODCACHE_SQLSERVER_CONNECTION` | `mcr.microsoft.com/mssql/server:2022-latest` container |

Containers are started with Testcontainers when Docker is available. With neither a variable nor Docker, the tests are skipped. Set `METHODCACHE_REQUIRE_INFRA_TESTS=true` to make a missing server fail the run instead; the `tests` CI job does this (`build/ci/jobs.sh`).

`RedisToxiproxyTests` always need Docker, even with `METHODCACHE_REDIS_URL` set. They put [Toxiproxy](https://github.com/Shopify/toxiproxy) (`ghcr.io/shopify/toxiproxy`) between MethodCache and a Redis container and make the network slow, silent or broken, checking that a cache miss still takes no longer than the operation timeout.

## Running them

From the repository root:

```bash
# Use servers you already run
METHODCACHE_REDIS_URL=localhost:6379 \
METHODCACHE_SQLSERVER_CONNECTION="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=true" \
dotnet test MethodCache.sln

# Or let Testcontainers start them (Docker must be running)
dotnet test tests/MethodCache.Redis.IntegrationTests
dotnet test tests/MethodCache.SqlServer.IntegrationTests
```

Each SQL Server test uses its own schema, and each Redis test its own key prefix and backplane channel, so tests do not see each other's data.

## SQL Server readiness

The SQL Server fixture opens a connection and runs a readiness query before cache operations start. Node setup also waits for `SqlServerTables.Creating`, because hosted startup can return while background table creation is still running. The tests give each distributed operation five seconds, for emulated SQL Server and concurrent framework runs; the library's default `OperationTimeout` is one second. `FixtureReadinessTests` cover pending creation, creation errors and cancellation.
