using System;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public interface IDistributedLock
    {
        Task<ILockHandle> AcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default);
    }

    public interface ILockHandle : IDisposable
    {
        bool IsAcquired { get; }
        string Resource { get; }
        Task RenewAsync(TimeSpan expiry);
    }
}