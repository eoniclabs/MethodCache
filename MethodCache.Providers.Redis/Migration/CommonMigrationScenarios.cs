using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Migration
{
    public static class CommonMigrationScenarios
    {
        /// <summary>
        /// Migrates data between different Redis databases
        /// </summary>
        public static async Task<MigrationResult> MigrateBetweenDatabasesAsync(
            IConnectionMultiplexer connection,
            ICacheMigrationTool migrationTool,
            ILoggerFactory loggerFactory,
            int sourceDatabase,
            int targetDatabase,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sourceLogger = loggerFactory.CreateLogger<RedisCacheSource>();
            var targetLogger = loggerFactory.CreateLogger<RedisCacheTarget>();
            
            var source = connection.AsSource(sourceLogger, sourceDatabase);
            var target = connection.AsTarget(targetLogger, targetDatabase);

            options ??= new MigrationOptions();

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }

        /// <summary>
        /// Migrates data between different Redis instances
        /// </summary>
        public static async Task<MigrationResult> MigrateBetweenInstancesAsync(
            IConnectionMultiplexer sourceConnection,
            IConnectionMultiplexer targetConnection,
            ICacheMigrationTool migrationTool,
            ILoggerFactory loggerFactory,
            int sourceDatabase = 0,
            int targetDatabase = 0,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sourceLogger = loggerFactory.CreateLogger<RedisCacheSource>();
            var targetLogger = loggerFactory.CreateLogger<RedisCacheTarget>();
            
            var source = sourceConnection.AsSource(sourceLogger, sourceDatabase);
            var target = targetConnection.AsTarget(targetLogger, targetDatabase);

            options ??= new MigrationOptions();

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }

        /// <summary>
        /// Migrates data with prefix transformation
        /// </summary>
        public static async Task<MigrationResult> MigrateWithPrefixChangeAsync(
            IConnectionMultiplexer connection,
            ICacheMigrationTool migrationTool,
            ILoggerFactory loggerFactory,
            string sourcePrefix,
            string targetPrefix,
            int database = 0,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sourceLogger = loggerFactory.CreateLogger<RedisCacheSource>();
            var targetLogger = loggerFactory.CreateLogger<RedisCacheTarget>();
            
            var source = connection.AsSource(sourceLogger, database, sourcePrefix);
            var target = connection.AsTarget(targetLogger, database, targetPrefix);

            options ??= new MigrationOptions();

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }

        /// <summary>
        /// Creates a backup of cache data
        /// </summary>
        public static async Task<MigrationResult> BackupCacheAsync(
            IConnectionMultiplexer sourceConnection,
            IConnectionMultiplexer backupConnection,
            ICacheMigrationTool migrationTool,
            ILoggerFactory loggerFactory,
            int sourceDatabase = 0,
            int backupDatabase = 0,
            CancellationToken cancellationToken = default)
        {
            var options = new MigrationOptions
            {
                OverwriteExisting = true,
                PreserveExpiry = true,
                PreserveTags = true,
                ValidateAfterMigration = true,
                BatchSize = 2000 // Larger batches for backup
            };

            return await MigrateBetweenInstancesAsync(
                sourceConnection,
                backupConnection,
                migrationTool,
                loggerFactory,
                sourceDatabase,
                backupDatabase,
                options,
                cancellationToken);
        }

        /// <summary>
        /// Restores cache data from backup
        /// </summary>
        public static async Task<MigrationResult> RestoreCacheAsync(
            IConnectionMultiplexer backupConnection,
            IConnectionMultiplexer targetConnection,
            ICacheMigrationTool migrationTool,
            ILoggerFactory loggerFactory,
            int backupDatabase = 0,
            int targetDatabase = 0,
            bool clearTargetFirst = false,
            CancellationToken cancellationToken = default)
        {
            // Optionally clear target database first
            if (clearTargetFirst)
            {
                var targetDb = targetConnection.GetDatabase(targetDatabase);
                var server = targetConnection.GetServers().First();
                await server.FlushDatabaseAsync(targetDatabase);
            }

            var options = new MigrationOptions
            {
                OverwriteExisting = true,
                PreserveExpiry = true,
                PreserveTags = true,
                ValidateAfterMigration = true,
                BatchSize = 2000
            };

            return await MigrateBetweenInstancesAsync(
                backupConnection,
                targetConnection,
                migrationTool,
                loggerFactory,
                backupDatabase,
                targetDatabase,
                options,
                cancellationToken);
        }

        /// <summary>
        /// Performs a dry run migration to estimate migration metrics
        /// </summary>
        public static async Task<MigrationResult> DryRunMigrationAsync(
            ICacheSource source,
            ICacheTarget target,
            ICacheMigrationTool migrationTool,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new MigrationOptions();
            options.DryRun = true;
            options.ValidateAfterMigration = false;

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }

        /// <summary>
        /// Migrates only entries matching specific patterns
        /// </summary>
        public static async Task<MigrationResult> MigrateByPatternsAsync(
            ICacheSource source,
            ICacheTarget target,
            ICacheMigrationTool migrationTool,
            string[] includePatterns,
            string[]? excludePatterns = null,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new MigrationOptions();
            options.KeyPatterns = includePatterns;
            options.ExcludeKeyPatterns = excludePatterns;

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }

        /// <summary>
        /// Migrates with TTL normalization (sets a consistent TTL for all entries)
        /// </summary>
        public static async Task<MigrationResult> MigrateWithNormalizedTTLAsync(
            ICacheSource source,
            ICacheTarget target,
            ICacheMigrationTool migrationTool,
            TimeSpan defaultTTL,
            MigrationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new MigrationOptions();
            options.DefaultTTL = defaultTTL;
            options.PreserveExpiry = false;

            return await migrationTool.MigrateAsync(source, target, options, cancellationToken);
        }
    }
}