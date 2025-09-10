# Redis Integration Tests

This project contains comprehensive integration tests for the MethodCache Redis provider that run against real Redis instances using Docker containers.

## Prerequisites

- Docker and Docker Compose
- .NET 9.0 SDK

## Running Tests

### Quick Start

```bash
# Start Redis container and run tests
docker-compose -f Docker/docker-compose.yml up -d redis-single
dotnet test

# Cleanup
docker-compose -f Docker/docker-compose.yml down
```

### Available Redis Configurations

The Docker Compose file provides several Redis setups for different testing scenarios:

- **redis-single**: Single Redis instance (default for tests)
- **redis-cluster-***: Redis cluster setup (3 nodes)
- **redis-sentinel-***: Redis Sentinel setup with failover

### Test Categories

1. **RedisCacheManagerIntegrationTests**: Core caching operations
   - Basic CRUD operations (Set, Get, Remove, Clear)
   - Complex object serialization/deserialization
   - Expiration behavior
   - Key existence checks

2. **RedisTagManagerIntegrationTests**: Tag-based invalidation
   - Single tag invalidation
   - Multiple tag invalidation
   - Tag isolation

3. **RedisPubSubIntegrationTests**: Cross-instance communication
   - Pub/Sub invalidation across multiple instances
   - Tag-based cross-instance invalidation

4. **RedisHealthCheckIntegrationTests**: Health monitoring
   - Basic health check functionality
   - Detailed health information
   - Custom health check names

## Test Infrastructure

- **Testcontainers**: Manages Docker containers during test execution
- **FluentAssertions**: Provides readable test assertions
- **RedisIntegrationTestBase**: Common base class with Redis container management

## Configuration

Tests use the `RedisIntegrationTestBase` which automatically:
- Starts a Redis container before each test class
- Configures the Redis cache provider
- Cleans up resources after tests complete

Each test gets a fresh Redis instance to ensure isolation.