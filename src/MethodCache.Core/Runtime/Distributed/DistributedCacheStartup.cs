using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.Core.Runtime.Distributed
{
    /// <summary>
    /// Starts the first contact with the distributed cache once the host's services have started (so a store's tables
    /// exist), in the background, so the connection is open when the first request needs it. The host doesn't wait
    /// for it, so an unreachable store never delays startup.
    /// </summary>
    internal sealed class DistributedCacheStartup(IServiceProvider services) : IHostedLifecycleService
    {
        public Task StartedAsync(CancellationToken cancellationToken)
        {
            try
            {
                _ = services.GetRequiredService<DistributedCacheLayer>().BeginFirstContact();
            }
            catch (Exception)
            {
                // A cache that can't be created is reported where it is first used, as without a host
            }

            return Task.CompletedTask;
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
