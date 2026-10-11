using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Monitoring
{
    public static class Setup
    {
        public static async Task Administrator(ICacheAdministrator admin)
        {
            // begin-snippet: monitoring-administrator
            var stats = await admin.GetStatsAsync();
            Console.WriteLine($"Hit ratio {stats.HitRatio:P0}, {stats.Entries} entries");

            foreach (var method in admin.GetMethods())
            {
                Console.WriteLine($"{method.MethodId}: {method.Duration}");
            }

            var entries = admin.ListEntries(tag: "user:42");
            await admin.InvalidateTagsAsync(["user:42"]);
            // end-snippet
        }
    }
}
