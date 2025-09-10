using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Migration
{
    public class RedisCacheMigrationTool : ICacheMigrationTool
    {
        private readonly ILogger<RedisCacheMigrationTool> _logger;
        private readonly ConcurrentDictionary<string, MigrationProgress> _activeProgress = new();

        public RedisCacheMigrationTool(ILogger<RedisCacheMigrationTool> logger)
        {
            _logger = logger;
        }

        public async Task<MigrationResult> MigrateAsync(
            ICacheSource source,
            ICacheTarget target,
            MigrationOptions options,
            CancellationToken cancellationToken = default)
        {
            var migrationId = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            var result = new MigrationResult { MigrationId = migrationId };
            var errors = new ConcurrentBag<string>();

            try
            {
                _logger.LogInformation("Starting cache migration {MigrationId}", migrationId);

                // Initialize progress tracking
                var totalEntries = await source.GetTotalCountAsync(cancellationToken);
                var progress = new MigrationProgress
                {
                    MigrationId = migrationId,
                    Status = MigrationStatus.InProgress,
                    TotalEntries = totalEntries,
                    CurrentOperation = "Initializing migration"
                };
                _activeProgress[migrationId] = progress;

                result.TotalEntries = totalEntries;

                // Create processing channel
                var channel = Channel.CreateUnbounded<CacheEntry>();
                var writer = channel.Writer;
                var reader = channel.Reader;

                // Start producer task
                var producerTask = ProduceEntriesAsync(source, writer, options, progress, cancellationToken);

                // Start consumer tasks
                var semaphore = new SemaphoreSlim(options.MaxConcurrency);
                var consumerTasks = Enumerable.Range(0, options.MaxConcurrency)
                    .Select(_ => ConsumeEntriesAsync(reader, target, options, progress, result, errors, semaphore, cancellationToken))
                    .ToArray();

                // Wait for all tasks to complete
                await Task.WhenAll(producerTask);
                writer.Complete();
                await Task.WhenAll(consumerTasks);

                progress.Status = MigrationStatus.Completed;
                progress.CurrentOperation = "Migration completed";

                _logger.LogInformation(
                    "Migration {MigrationId} completed. Total: {Total}, Migrated: {Migrated}, Skipped: {Skipped}, Failed: {Failed}",
                    migrationId, result.TotalEntries, result.MigratedEntries, result.SkippedEntries, result.FailedEntries);

                // Perform validation if requested
                if (options.ValidateAfterMigration && !options.DryRun)
                {
                    progress.CurrentOperation = "Validating migration";
                    var validation = await ValidateMigrationAsync(source, target, cancellationToken);
                    result.Statistics["validation"] = validation;
                }

                result.Success = result.FailedEntries == 0;
                result.Errors = errors.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration {MigrationId} failed", migrationId);
                result.Success = false;
                result.Errors.Add($"Migration failed: {ex.Message}");
                
                if (_activeProgress.TryGetValue(migrationId, out var progress))
                {
                    progress.Status = MigrationStatus.Failed;
                    progress.CurrentOperation = $"Failed: {ex.Message}";
                }
            }
            finally
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                
                // Clean up progress tracking after a delay
                _ = Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None)
                    .ContinueWith(_ => _activeProgress.TryRemove(migrationId, out var _));
            }

            return result;
        }

        public async Task<ValidationResult> ValidateMigrationAsync(
            ICacheSource source,
            ICacheTarget target,
            CancellationToken cancellationToken = default)
        {
            var result = new ValidationResult();
            var errors = new List<string>();
            var missingKeys = new List<string>();

            try
            {
                _logger.LogInformation("Starting migration validation");

                var sourceCount = await source.GetTotalCountAsync(cancellationToken);
                result.SourceCount = sourceCount;

                var processedCount = 0L;
                var targetExistingCount = 0L;

                await foreach (var entry in await source.GetAllEntriesAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var exists = await target.ExistsAsync(entry.Key, cancellationToken);
                        if (exists)
                        {
                            targetExistingCount++;
                        }
                        else
                        {
                            missingKeys.Add(entry.Key);
                        }

                        processedCount++;
                        
                        if (processedCount % 1000 == 0)
                        {
                            _logger.LogDebug("Validation progress: {Processed}/{Total}", processedCount, sourceCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error validating key '{entry.Key}': {ex.Message}");
                    }
                }

                result.TargetCount = targetExistingCount;
                result.MissingKeys = missingKeys;
                result.Errors = errors;
                result.IsValid = missingKeys.Count == 0 && errors.Count == 0;

                _logger.LogInformation(
                    "Validation completed. Source: {SourceCount}, Target: {TargetCount}, Missing: {MissingCount}, Errors: {ErrorCount}",
                    result.SourceCount, result.TargetCount, result.MissingKeys.Count, result.Errors.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed");
                result.Errors.Add($"Validation failed: {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        public async Task<MigrationProgress> GetProgressAsync(string migrationId)
        {
            await Task.CompletedTask; // Make async for consistency
            
            if (_activeProgress.TryGetValue(migrationId, out var progress))
            {
                return progress;
            }

            return new MigrationProgress
            {
                MigrationId = migrationId,
                Status = MigrationStatus.NotStarted
            };
        }

        private async Task ProduceEntriesAsync(
            ICacheSource source,
            ChannelWriter<CacheEntry> writer,
            MigrationOptions options,
            MigrationProgress progress,
            CancellationToken cancellationToken)
        {
            try
            {
                progress.CurrentOperation = "Reading source entries";
                
                await foreach (var entry in await source.GetAllEntriesAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Apply key filtering
                    if (!ShouldProcessKey(entry.Key, options))
                        continue;

                    await writer.WriteAsync(entry, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading source entries");
                throw;
            }
        }

        private async Task ConsumeEntriesAsync(
            ChannelReader<CacheEntry> reader,
            ICacheTarget target,
            MigrationOptions options,
            MigrationProgress progress,
            MigrationResult result,
            ConcurrentBag<string> errors,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            var batch = new List<CacheEntry>(options.BatchSize);
            
            try
            {
                await foreach (var entry in reader.ReadAllAsync(cancellationToken))
                {
                    batch.Add(entry);
                    
                    if (batch.Count >= options.BatchSize)
                    {
                        await ProcessBatch(batch, target, options, progress, result, errors, semaphore, cancellationToken);
                        batch.Clear();
                    }
                }

                // Process remaining entries
                if (batch.Count > 0)
                {
                    await ProcessBatch(batch, target, options, progress, result, errors, semaphore, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing entries");
                errors.Add($"Consumer error: {ex.Message}");
            }
        }

        private async Task ProcessBatch(
            List<CacheEntry> batch,
            ICacheTarget target,
            MigrationOptions options,
            MigrationProgress progress,
            MigrationResult result,
            ConcurrentBag<string> errors,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
            
            try
            {
                progress.CurrentOperation = $"Processing batch of {batch.Count} entries";
                
                if (options.DryRun)
                {
                    // In dry run mode, just count the entries
                    result.MigratedEntries += batch.Count;
                    progress.ProcessedEntries += batch.Count;
                    return;
                }

                var entriesToMigrate = new List<CacheEntry>();

                foreach (var entry in batch)
                {
                    try
                    {
                        // Check if entry should be overwritten
                        if (!options.OverwriteExisting && await target.ExistsAsync(entry.Key, cancellationToken))
                        {
                            result.SkippedEntries++;
                            continue;
                        }

                        // Apply TTL transformations
                        if (options.DefaultTTL.HasValue && (!options.PreserveExpiry || !entry.Expiry.HasValue))
                        {
                            entry.Expiry = DateTime.UtcNow.Add(options.DefaultTTL.Value);
                        }

                        // Remove tags if not preserving
                        if (!options.PreserveTags)
                        {
                            entry.Tags.Clear();
                        }

                        entriesToMigrate.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error preparing entry '{entry.Key}': {ex.Message}");
                        result.FailedEntries++;
                    }
                }

                if (entriesToMigrate.Count > 0)
                {
                    await target.SetEntriesAsync(entriesToMigrate, cancellationToken);
                    result.MigratedEntries += entriesToMigrate.Count;
                }

                progress.ProcessedEntries += batch.Count;
                
                // Update estimated remaining time
                UpdateProgressEstimates(progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch");
                errors.Add($"Batch processing error: {ex.Message}");
                result.FailedEntries += batch.Count;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void UpdateProgressEstimates(MigrationProgress progress)
        {
            if (progress.ProcessedEntries > 0)
            {
                var rate = progress.ProcessedEntries / progress.Elapsed.TotalSeconds;
                var remaining = progress.TotalEntries - progress.ProcessedEntries;
                progress.EstimatedRemaining = TimeSpan.FromSeconds(remaining / Math.Max(rate, 0.1));
            }
        }

        private bool ShouldProcessKey(string key, MigrationOptions options)
        {
            // Check exclude patterns first
            if (options.ExcludeKeyPatterns?.Any(pattern => MatchesPattern(key, pattern)) == true)
                return false;

            // Check include patterns
            if (options.KeyPatterns?.Any() == true)
                return options.KeyPatterns.Any(pattern => MatchesPattern(key, pattern));

            return true;
        }

        private bool MatchesPattern(string key, string pattern)
        {
            // Simple wildcard pattern matching
            if (pattern.Contains('*'))
            {
                var parts = pattern.Split('*');
                var index = 0;
                
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (string.IsNullOrEmpty(part))
                        continue;

                    var foundIndex = key.IndexOf(part, index);
                    if (foundIndex == -1)
                        return false;

                    // First part must match from the beginning
                    if (i == 0 && foundIndex != 0)
                        return false;

                    // Last part must match to the end
                    if (i == parts.Length - 1 && foundIndex + part.Length != key.Length)
                        return false;

                    index = foundIndex + part.Length;
                }

                return true;
            }

            return key == pattern;
        }
    }
}