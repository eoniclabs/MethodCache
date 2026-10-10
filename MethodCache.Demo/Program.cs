using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Demo
{
    public interface IDemoService
    {
        [Cache(Duration = "00:05:00", Tags = new[] { "demo" })]
        Task<string> GetDataAsync(string key);

        [CacheInvalidate(Tags = new[] { "demo" })]
        Task ClearCacheAsync();
    }

    public class DemoService : IDemoService
    {
        public async Task<string> GetDataAsync(string key)
        {
            await Task.Delay(100); // Simulate slow work
            return $"Demo data for {key} at {DateTime.Now:HH:mm:ss.fff}";
        }

        public Task ClearCacheAsync() => Task.CompletedTask;
    }

    class Program
    {
        static async Task Main()
        {
            Console.WriteLine("=== MethodCache registration demo ===\n");

            // 1. Register your services as usual, with any lifetime, then call AddMethodCache().
            //    Your registration keeps its lifetime and is wrapped in the generated cache decorator.
            var services = new ServiceCollection();
            services.AddScoped<IDemoService, DemoService>();
            services.AddMethodCache();
            await RunAsync("Your own registration (scoped)", services);

            // 2. Or let AddMethodCache() find and register the implementation for you.
            var scanned = new ServiceCollection();
            scanned.AddMethodCache();
            await RunAsync("Registered by AddMethodCache()", scanned);
        }

        static async Task RunAsync(string title, IServiceCollection services)
        {
            Console.WriteLine(title);

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDemoService>();
            Console.WriteLine($"  Resolved: {service.GetType().Name}");

            for (var i = 1; i <= 2; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                var value = await service.GetDataAsync("key");
                Console.WriteLine($"  Call {i}: {stopwatch.ElapsedMilliseconds,3} ms  {value}");
            }

            await service.ClearCacheAsync();
            var afterClear = Stopwatch.StartNew();
            await service.GetDataAsync("key");
            Console.WriteLine($"  After invalidation: {afterClear.ElapsedMilliseconds,3} ms\n");
        }
    }
}
