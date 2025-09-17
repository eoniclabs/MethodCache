using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Services
{
    /// <summary>
    /// No-operation hosted service used when cache warming is disabled.
    /// Prevents unnecessary service registration and startup overhead.
    /// </summary>
    internal class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}