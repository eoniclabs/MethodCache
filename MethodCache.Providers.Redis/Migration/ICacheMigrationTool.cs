using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Migration
{
    public interface ICacheMigrationTool
    {
        /// <summary>
        /// Migrates data from source cache to target cache
        /// </summary>
        Task<MigrationResult> MigrateAsync(
            ICacheSource source,
            ICacheTarget target,
            MigrationOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that migration was successful
        /// </summary>
        Task<ValidationResult> ValidateMigrationAsync(
            ICacheSource source,
            ICacheTarget target,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets migration progress information
        /// </summary>
        Task<MigrationProgress> GetProgressAsync(string migrationId);
    }

    public interface ICacheSource
    {
        Task<IAsyncEnumerable<CacheEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default);
        Task<long> GetTotalCountAsync(CancellationToken cancellationToken = default);
        Task<CacheEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default);
    }

    public interface ICacheTarget
    {
        Task SetEntryAsync(CacheEntry entry, CancellationToken cancellationToken = default);
        Task SetEntriesAsync(IEnumerable<CacheEntry> entries, CancellationToken cancellationToken = default);
        Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
    }

    public class CacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public byte[] Value { get; set; } = Array.Empty<byte>();
        public DateTime? Expiry { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
        public HashSet<string> Tags { get; set; } = new();
    }

    public class MigrationOptions
    {
        public int BatchSize { get; set; } = 1000;
        public int MaxConcurrency { get; set; } = 4;
        public bool OverwriteExisting { get; set; } = false;
        public bool PreserveExpiry { get; set; } = true;
        public bool PreserveTags { get; set; } = true;
        public bool ValidateAfterMigration { get; set; } = true;
        public string[]? KeyPatterns { get; set; }
        public string[]? ExcludeKeyPatterns { get; set; }
        public TimeSpan? DefaultTTL { get; set; }
        public bool DryRun { get; set; } = false;
    }

    public class MigrationResult
    {
        public string MigrationId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public long TotalEntries { get; set; }
        public long MigratedEntries { get; set; }
        public long SkippedEntries { get; set; }
        public long FailedEntries { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> Errors { get; set; } = new();
        public Dictionary<string, object> Statistics { get; set; } = new();
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public long SourceCount { get; set; }
        public long TargetCount { get; set; }
        public List<string> MissingKeys { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public Dictionary<string, object> Statistics { get; set; } = new();
    }

    public class MigrationProgress
    {
        public string MigrationId { get; set; } = string.Empty;
        public MigrationStatus Status { get; set; }
        public long TotalEntries { get; set; }
        public long ProcessedEntries { get; set; }
        public long MigratedEntries { get; set; }
        public long FailedEntries { get; set; }
        public double ProgressPercentage => TotalEntries > 0 ? (double)ProcessedEntries / TotalEntries * 100 : 0;
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }
        public string? CurrentOperation { get; set; }
        public List<string> RecentErrors { get; set; } = new();
    }

    public enum MigrationStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }
}