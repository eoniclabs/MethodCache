using System.Diagnostics;
using MethodCache;
using MethodCache.Extensibility;
using MethodCache.L2Bench;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Times Redis, SQL Server and PostgreSQL as MethodCache's distributed cache. See README.md for how to run it.
var mode = args.Length > 0 ? args[0] : "all";
if (mode is not ("all" or "raw" or "throughput" or "backplane" or "e2e" or "miss"))
{
    Console.Error.WriteLine("Usage: dotnet run -c Release -- [all|raw|throughput|backplane|e2e|miss]");
    return 1;
}

if (mode is "raw" or "all") await Harness.RawAsync();
if (mode is "throughput" or "all") await Harness.ThroughputAsync();
if (mode is "backplane" or "all") await Harness.BackplaneAsync();
if (mode is "e2e" or "all") await Harness.EndToEndAsync();
if (mode is "miss") await Harness.MissAsync();
return 0;

namespace MethodCache.L2Bench
{
    public static class Env
    {
        public static string RedisUrl => Environment.GetEnvironmentVariable("REDIS") ?? "localhost:6379";
        public static string SqlConnection => Environment.GetEnvironmentVariable("SQL") ?? throw new InvalidOperationException("Set SQL to a SQL Server connection string.");
        public static string PgConnection => Environment.GetEnvironmentVariable("PG") ?? throw new InvalidOperationException("Set PG to a PostgreSQL connection string.");
        public static TimeSpan Poll => TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("POLL_MS"), out var ms) ? ms : 1000);

        public static ServiceProvider Provider(string backend, string prefix, Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            configure?.Invoke(services);
            services.AddMethodCache(mc =>
            {
                mc.Distributed(o => o.KeyPrefix = prefix);
                switch (backend)
                {
                    case "Redis": mc.UseRedis(RedisUrl); break;
                    case "SqlServer": mc.UseSqlServer(o => { o.ConnectionString = SqlConnection; o.BackplanePollingInterval = Poll; }); break;
                    case "PostgreSql": mc.UsePostgreSql(o => { o.ConnectionString = PgConnection; o.BackplanePollingInterval = Poll; }); break;
                    default: throw new InvalidOperationException($"Unknown backend {backend}: use Redis, SqlServer or PostgreSql.");
                }
            });
            var provider = services.BuildServiceProvider();
            foreach (var hosted in provider.GetServices<IHostedService>()) hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Thread.Sleep(1500);
            return provider;
        }

        public static IDistributedCache Cache(ServiceProvider provider) => provider.GetRequiredKeyedService<IDistributedCache>("MethodCache");
    }

    public sealed record Product(int Id, string Name, string Description);

    public interface ICatalog
    {
        [Cache(Duration = "30m", Tags = ["p:{id}"])]
        Task<Product> GetAsync(int id);
    }

    public sealed class Catalog : ICatalog
    {
        private static readonly string Description = new('x', 900);
        public int Calls;

        public Task<Product> GetAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new Product(id, "Product " + id, Description));
        }
    }

    public static class Harness
    {
        public static readonly string[] Backends = (Environment.GetEnvironmentVariable("BACKENDS") ?? "Redis,SqlServer,PostgreSql").Split(',');
        private static int Seconds => int.TryParse(Environment.GetEnvironmentVariable("SECONDS_PER_RUN"), out var s) ? s : 10;

        private static string Stats(List<double> us)
        {
            if (us.Count == 0) return "n=0";
            us.Sort();
            double P(double p) => us[Math.Min(us.Count - 1, (int)(us.Count * p))];
            return $"n={us.Count} mean={us.Average():F0}us p50={P(0.50):F0}us p90={P(0.90):F0}us p99={P(0.99):F0}us max={us[^1]:F0}us";
        }

        private static double Us(long from, long to) => (to - from) * 1_000_000.0 / Stopwatch.Frequency;

        /// <summary>
        /// One call at a time through each provider's own IDistributedCache, by size: read a value, read a missing key,
        /// write a new value, and replace a live one.
        /// </summary>
        public static async Task RawAsync()
        {
            Console.WriteLine("## Raw IDistributedCache, one call at a time");
            var run = Guid.NewGuid().ToString("N");
            foreach (var backend in Backends)
            {
                await using var provider = Env.Provider(backend, "raw:");
                var cache = Env.Cache(provider);
                var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
                foreach (var size in new[] { 256, 4096, 65536 })
                {
                    var value = new byte[size];
                    Random.Shared.NextBytes(value);
                    await cache.SetAsync($"hit:{size}", value, options);
                    for (var k = 0; k < 1000; k++) await cache.SetAsync($"live:{size}:{k}", value, options);
                    var hit = new List<double>(); var miss = new List<double>(); var add = new List<double>(); var replace = new List<double>();
                    for (var i = 0; i < 4000; i++)
                    {
                        var t = Stopwatch.GetTimestamp();
                        _ = await cache.GetAsync($"hit:{size}") ?? throw new InvalidOperationException("miss");
                        if (i >= 500) hit.Add(Us(t, Stopwatch.GetTimestamp()));
                        t = Stopwatch.GetTimestamp();
                        _ = await cache.GetAsync($"missing:{size}:{i}");
                        if (i >= 500) miss.Add(Us(t, Stopwatch.GetTimestamp()));
                        t = Stopwatch.GetTimestamp();
                        await cache.SetAsync($"new:{run}:{size}:{i}", value, options);
                        if (i >= 500) add.Add(Us(t, Stopwatch.GetTimestamp()));
                        t = Stopwatch.GetTimestamp();
                        await cache.SetAsync($"live:{size}:{i % 1000}", value, options);
                        if (i >= 500) replace.Add(Us(t, Stopwatch.GetTimestamp()));
                    }

                    Console.WriteLine($"{backend,-10} {size,6} B get hit  {Stats(hit)}");
                    Console.WriteLine($"{backend,-10} {size,6} B get miss {Stats(miss)}");
                    Console.WriteLine($"{backend,-10} {size,6} B set new  {Stats(add)}");
                    Console.WriteLine($"{backend,-10} {size,6} B replace  {Stats(replace)}");
                }
            }
        }

        public static async Task ThroughputAsync()
        {
            Console.WriteLine("## Throughput: 1 KB values, 10,000 keys, 90 % reads / 10 % writes");
            foreach (var workers in new[] { 1, 16, 64 })
                foreach (var backend in Backends)
                {
                    await using var provider = Env.Provider(backend, "tp:");
                    var cache = Env.Cache(provider);
                    var value = new byte[1024];
                    var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
                    for (var i = 0; i < 10_000; i += 100)
                        await Task.WhenAll(Enumerable.Range(i, 100).Select(k => cache.SetAsync($"tp:{k}", value, options)));
                    await RunAsync(cache, workers, TimeSpan.FromSeconds(2), value, options, null, null);
                    var reads = new List<double>(); var writes = new List<double>();
                    var ops = await RunAsync(cache, workers, TimeSpan.FromSeconds(Seconds), value, options, reads, writes);
                    Console.WriteLine($"{backend,-10} workers={workers,-3} {ops / Seconds,8:N0} ops/s | reads {Stats(reads)} | writes {Stats(writes)}");
                }
        }

        private static readonly object Gate = new();

        private static async Task<long> RunAsync(IDistributedCache cache, int workers, TimeSpan duration, byte[] value,
            DistributedCacheEntryOptions options, List<double>? reads, List<double>? writes)
        {
            var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            long total = 0;
            await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
            {
                var random = new Random(w);
                var r = new List<double>(); var wr = new List<double>();
                long count = 0;
                while (Stopwatch.GetTimestamp() < end)
                {
                    var key = $"tp:{random.Next(10_000)}";
                    var t0 = Stopwatch.GetTimestamp();
                    if (random.Next(10) == 0) { await cache.SetAsync(key, value, options); wr.Add(Us(t0, Stopwatch.GetTimestamp())); }
                    else { _ = await cache.GetAsync(key) ?? throw new InvalidOperationException("miss"); r.Add(Us(t0, Stopwatch.GetTimestamp())); }
                    count++;
                }

                Interlocked.Add(ref total, count);
                lock (Gate) { reads?.AddRange(r); writes?.AddRange(wr); }
            })));
            return total;
        }

        public static async Task BackplaneAsync()
        {
            Console.WriteLine($"## Backplane: publish on instance A until instance B's callback runs (500 messages, 5 ms apart, polling {Env.Poll.TotalMilliseconds} ms)");
            foreach (var backend in Backends)
            {
                await using var pa = Env.Provider(backend, "bp:");
                await using var pb = Env.Provider(backend, "bp:");
                var a = pa.GetRequiredKeyedService<IBackplane>("MethodCache");
                var b = pb.GetRequiredKeyedService<IBackplane>("MethodCache");
                var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
                var delivered = new List<double>(); var publish = new List<double>();
                await b.SubscribeAsync(message =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (message.Key != null && pending.TryRemove(message.Key, out var sent)) lock (Gate) delivered.Add(Us(sent, now));
                    return Task.CompletedTask;
                });
                await Task.Delay(1500);

                for (var i = 0; i < 550; i++)
                {
                    var key = $"bp:{backend}:{i}";
                    var t0 = Stopwatch.GetTimestamp();
                    pending[key] = t0;
                    await a.PublishInvalidationAsync(key);
                    if (i >= 50) publish.Add(Us(t0, Stopwatch.GetTimestamp()));
                    await Task.Delay(5);
                    if (i == 49) lock (Gate) delivered.Clear();
                }

                await Task.Delay(3000);
                await b.UnsubscribeAsync();
                Console.WriteLine($"{backend,-10} publish returned {Stats(publish)}");
                Console.WriteLine($"{backend,-10} delivered        {Stats(delivered)} lost={pending.Keys.Count(k => int.Parse(k.Split(':')[2]) >= 50)}");
            }
        }

        private static ServiceProvider Instance(string backend, long maxItems) => Env.Provider(backend, "e2e:", services =>
        {
            services.AddSingleton<Catalog>();
            services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<Catalog>());
            services.AddMethodCache(mc => mc.Memory(o => o.MaxItems = maxItems));
        });

        public static async Task MissAsync()
        {
            Console.WriteLine("## Miss path only");
            var run = Random.Shared.Next(1, 1000) * 1_000_000;
            foreach (var backend in Backends)
            {
                await using var reader = Instance(backend, 100);
                var r = reader.GetRequiredService<ICatalog>();
                for (var i = 0; i < 200; i++) await r.GetAsync(run + 700_000 + i);
                var miss = new List<double>();
                for (var i = 0; i < 1000; i++) { var t = Stopwatch.GetTimestamp(); await r.GetAsync(run + 500_000 + i); miss.Add(Us(t, Stopwatch.GetTimestamp())); }
                Console.WriteLine($"{backend,-10} miss     {Stats(miss)}");
            }
        }

        public static async Task EndToEndAsync()
        {
            Console.WriteLine("## MethodCache end to end (generated decorator, JSON serializer, ~1 KB result)");
            var run = Random.Shared.Next(1, 1000) * 1_000_000;
            foreach (var backend in Backends)
            {
                await using var writer = Instance(backend, 100_000);
                await using var reader = Instance(backend, 100);
                var w = writer.GetRequiredService<ICatalog>();
                var r = reader.GetRequiredService<ICatalog>();
                var readerCalls = reader.GetRequiredService<Catalog>();

                await w.GetAsync(run);
                var l1 = new List<double>();
                for (var i = 0; i < 200_000; i++) { var t = Stopwatch.GetTimestamp(); await w.GetAsync(run); l1.Add(Us(t, Stopwatch.GetTimestamp())); }

                // Fill L2 from the writer, then read every key on the reader: its small memory cache misses, L2 hits
                const int n = 5_000;
                for (var i = 0; i < n; i += 100) await Task.WhenAll(Enumerable.Range(run + i, 100).Select(k => w.GetAsync(k)));
                var l2Hit = new List<double>();
                for (var i = 0; i < n; i++) { var t = Stopwatch.GetTimestamp(); await r.GetAsync(run + i); l2Hit.Add(Us(t, Stopwatch.GetTimestamp())); }
                var callsAfterHits = readerCalls.Calls;

                // Full miss: neither layer has it, so the method runs and its result is written to L2
                var miss = new List<double>();
                for (var i = 0; i < 2_000; i++) { var t = Stopwatch.GetTimestamp(); await r.GetAsync(run + 500_000 + i); miss.Add(Us(t, Stopwatch.GetTimestamp())); }

                Console.WriteLine($"{backend,-10} L1 hit   {Stats(l1)}");
                Console.WriteLine($"{backend,-10} L2 hit   {Stats(l2Hit)} (method ran {callsAfterHits} of {n} times)");
                Console.WriteLine($"{backend,-10} miss     {Stats(miss)}");

                // Both instances hold the value in memory; A invalidates its tag; time until B runs the method again
                var managerA = writer.GetRequiredService<ICacheManager>();
                await using var other = Instance(backend, 100_000);
                var b = other.GetRequiredService<ICatalog>();
                var bCalls = other.GetRequiredService<Catalog>();
                await b.GetAsync(-1);
                await Task.Delay(1500);
                var invalidation = new List<double>();
                for (var i = 0; i < 200; i++)
                {
                    var id = run + 900_000 + i;
                    await w.GetAsync(id);
                    await b.GetAsync(id);
                    var calls = bCalls.Calls;
                    var t = Stopwatch.GetTimestamp();
                    await managerA.InvalidateTagsAsync([$"p:{id}"]);
                    var deadline = t + 3 * Stopwatch.Frequency;
                    while (bCalls.Calls == calls && Stopwatch.GetTimestamp() < deadline) { await b.GetAsync(id); await Task.Yield(); }
                    if (bCalls.Calls != calls) invalidation.Add(Us(t, Stopwatch.GetTimestamp()));
                    await Task.Delay(5);
                }

                Console.WriteLine($"{backend,-10} tag invalidation seen by other instance: {Stats(invalidation)} (of 200)");
            }
        }
    }
}
