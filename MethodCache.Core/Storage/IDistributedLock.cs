using System;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Core.Storage
{
    /// <summary>
    /// Provides distributed locking capabilities across multiple instances
    /// </summary>
    public interface IDistributedLock
    {
        /// <summary>
        /// Attempts to acquire a distributed lock for the specified resource
        /// </summary>
        /// <param name="resource">The resource to lock</param>
        /// <param name="expiry">How long the lock should be held if not released</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A lock handle that can be used to manage the lock</returns>
        Task<ILockHandle> AcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Handle for managing a distributed lock
    /// </summary>
    public interface ILockHandle : IDisposable
    {
        /// <summary>
        /// Whether the lock was successfully acquired
        /// </summary>
        bool IsAcquired { get; }

        /// <summary>
        /// The resource that was locked
        /// </summary>
        string Resource { get; }

        /// <summary>
        /// Renews the lock for the specified duration
        /// </summary>
        /// <param name="expiry">New expiration time</param>
        Task RenewAsync(TimeSpan expiry);
    }
}