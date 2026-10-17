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

## Policy invalidation faults

The Redis and SQL policy suites compile the same `NativePolicyFaultContract` as the Core fault model.
It holds or fails selected stamp writes and fails one publication around the real provider, while
successful operations still reach that provider. Tests check exact partial confirmation counters,
independent recovery queues, caller cancellation after local mutation and validation before mutation.
They read confirmed stamp bytes back from the backend; wrapper counters alone cannot pass the test.
Each scenario owns its metadata prefix and SQL schema.

The shared policy state contract also covers reordered policy sources, reload identity changes,
Enabled specificity, custom managers, held loads/refreshes and sync calls during a blocked async
read. A connected delivery gate observes and drops a real native method frame to test lost-message
semantics without using unsubscription as a timing barrier. Tests await actual policy invalidation
completion before asserting shared L2 reuse after reload; reload notification alone is a local guarantee.
