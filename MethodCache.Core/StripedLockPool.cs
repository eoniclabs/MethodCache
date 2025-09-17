using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Core
{
    /// <summary>
    /// High-performance striped lock pool that eliminates per-key SemaphoreSlim allocation and dictionary contention.
    /// Uses a fixed number of SemaphoreSlim instances mapped by key hash to provide per-key locking semantics
    /// without the overhead of creating/disposing locks or ConcurrentDictionary operations.
    /// </summary>
    public sealed class StripedLockPool : IDisposable
    {
        private readonly SemaphoreSlim[] _locks;
        private readonly int _stripeMask;
        private bool _disposed;

        /// <summary>
        /// Creates a striped lock pool with the specified number of stripes.
        /// </summary>
        /// <param name="stripeCount">Number of lock stripes. Must be a power of 2 for optimal performance. Defaults to 128.</param>
        public StripedLockPool(int stripeCount = 128)
        {
            // Ensure stripe count is power of 2 for efficient modulo operation
            if (stripeCount <= 0 || (stripeCount & (stripeCount - 1)) != 0)
            {
                throw new ArgumentException("Stripe count must be a positive power of 2", nameof(stripeCount));
            }

            _locks = new SemaphoreSlim[stripeCount];
            _stripeMask = stripeCount - 1;

            // Initialize all semaphores
            for (int i = 0; i < stripeCount; i++)
            {
                _locks[i] = new SemaphoreSlim(1, 1);
            }
        }

        /// <summary>
        /// Acquires a lock for the specified key and executes the provided action.
        /// </summary>
        /// <param name="key">The key to lock on</param>
        /// <param name="action">The action to execute while holding the lock</param>
        /// <param name="cancellationToken">Cancellation token</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task ExecuteWithLockAsync(string key, Func<Task> action, CancellationToken cancellationToken = default)
        {
            var semaphore = GetSemaphore(key);
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Acquires a lock for the specified key and executes the provided function.
        /// </summary>
        /// <typeparam name="T">Return type</typeparam>
        /// <param name="key">The key to lock on</param>
        /// <param name="func">The function to execute while holding the lock</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The result of the function</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<T> ExecuteWithLockAsync<T>(string key, Func<Task<T>> func, CancellationToken cancellationToken = default)
        {
            var semaphore = GetSemaphore(key);
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await func().ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Gets the semaphore for a given key using efficient hash-based stripe selection.
        /// </summary>
        /// <param name="key">The key to get semaphore for</param>
        /// <returns>The semaphore for the key's stripe</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SemaphoreSlim GetSemaphore(string key)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StripedLockPool));

            // Use fast hash-based stripe selection
            // GetHashCode() is sufficient for distribution across stripes
            var hash = key.GetHashCode();
            var stripeIndex = hash & _stripeMask;
            return _locks[stripeIndex];
        }

        /// <summary>
        /// Tries to acquire a lock for the specified key without blocking.
        /// </summary>
        /// <param name="key">The key to try lock on</param>
        /// <returns>A disposable lock handle if successful, null if lock could not be acquired</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StripedLockHandle? TryAcquireLock(string key)
        {
            var semaphore = GetSemaphore(key);
            if (semaphore.Wait(0))
            {
                return new StripedLockHandle(semaphore);
            }
            return null;
        }

        /// <summary>
        /// Acquires a lock for the specified key.
        /// </summary>
        /// <param name="key">The key to lock on</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A disposable lock handle</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<StripedLockHandle> AcquireLockAsync(string key, CancellationToken cancellationToken = default)
        {
            var semaphore = GetSemaphore(key);
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new StripedLockHandle(semaphore);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                for (int i = 0; i < _locks.Length; i++)
                {
                    _locks[i]?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Disposable handle for a striped lock that automatically releases when disposed.
    /// </summary>
    public readonly struct StripedLockHandle : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        internal StripedLockHandle(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            _semaphore?.Release();
        }
    }
}