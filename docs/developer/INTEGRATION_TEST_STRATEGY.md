# Integration Tests

The Redis and SQL Server providers each have an integration test project that runs two MethodCache instances against one real server. The tests check that a value one instance stores is read by the other, and that key and tag invalidations reach the other instance's in-memory cache through the backplane.

## Where the server comes from

| Project | Environment variable | Fallback |
|---------|----------------------|----------|
| `MethodCache.Redis.IntegrationTests` | `METHODCACHE_REDIS_URL` | `redis:7-alpine` container |
| `MethodCache.SqlServer.IntegrationTests` | `METHODCACHE_SQLSERVER_CONNECTION` | `mcr.microsoft.com/mssql/server:2022-latest` container |

Containers are started with Testcontainers when Docker is available. With neither a variable nor Docker, the tests are skipped. Set `METHODCACHE_REQUIRE_INFRA_TESTS=true` to make a missing server fail the run instead; the publish workflow does this.

## Running them

```bash
# Use servers you already run
METHODCACHE_REDIS_URL=localhost:6379 \
METHODCACHE_SQLSERVER_CONNECTION="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=true" \
dotnet test

# Or let Testcontainers start them (Docker must be running)
dotnet test
```

Each SQL Server test uses its own schema, and each Redis test its own key prefix and backplane channel, so tests do not see each other's data.
