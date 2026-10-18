using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using StackExchange.Redis;

// Measures what each design harness case costs per operation: allocated bytes, Redis commands (counted by the
// server, so commands inside Lua scripts count too) and SQL commands with their text length. These counts are
// deterministic, so comparing two revisions shows extra work in minutes, without a timing series.
//
// Usage: MethodCache.CostProfile [case filter] [--exact] [--out results.jsonl] [--stacks seconds | --time | --list]
// The filter selects the cases whose name contains it, or with --exact the one case with that name.
// --stacks attributes the allocations of each matching case to call sites instead (EventPipe allocation ticks).
// --time measures the median time per operation with BenchmarkDotNet in this process; cost-profile.sh --time runs
// it for one case per process, alternately for two revisions, and compares the pairs. --list prints the case names.
// Redis and SQL Server cases run when METHODCACHE_REDIS_URL and METHODCACHE_SQLSERVER_CONNECTION are set.
internal static class Program
{
    private static readonly Type[] Types =
    [
        typeof(MethodCache.Benchmarks.Comparison.GeneratedContractBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixHitPathBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.HttpResponsePathBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.HttpColdMissBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.DesignMemoryWorkloadBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.DesignL2WorkloadBenchmarks),
        typeof(MethodCache.Benchmarks.Microbenchmarks.DesignHttpWorkloadBenchmarks),
    ];

    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !IsOptionValue(args, a)) ?? "";
        var output = Option(args, "--out") ?? "cost-profile.jsonl";
        var stacks = Option(args, "--stacks") is { } seconds ? double.Parse(seconds, CultureInfo.InvariantCulture) : (double?)null;
        var redisUrl = Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL");
        var hasSql = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_CONNECTION") != null;
        var exact = args.Contains("--exact");
        bool Matches(string name) => exact ? name == filter : name.Contains(filter, StringComparison.Ordinal);
        if (args.Contains("--time")) return await Timing.RunAsync(Types, Matches, redisUrl != null, hasSql, output);
        if (args.Contains("--list"))
        {
            foreach (var (type, parameters, method) in Cases())
            {
                var name = Name(type, parameters, method);
                var available = (redisUrl != null || !parameters.Values.Any(v => Equals(v, "Redis"))) && (hasSql || !parameters.Values.Any(v => Equals(v, "SqlServer")));
                if (available && Matches(name)) Console.WriteLine(name);
            }
            return 0;
        }
        using var redis = redisUrl == null ? null : await ConnectionMultiplexer.ConnectAsync(redisUrl + ",allowAdmin=true");
        await using var writer = new StreamWriter(output, append: false);
        foreach (var (type, parameters, method) in Cases())
        {
            var name = Name(type, parameters, method);
            if (!Matches(name)) continue;
            if ((redis == null && parameters.Values.Any(v => Equals(v, "Redis"))) || (!hasSql && parameters.Values.Any(v => Equals(v, "SqlServer"))))
            {
                Console.WriteLine($"{name} skipped: no backend configured");
                continue;
            }
            Dictionary<string, object> result;
            try { result = await Measure(type, parameters, method, redis, stacks); }
            catch (Exception error) { result = new() { ["error"] = error.GetBaseException().Message }; }
            result["case"] = name;
            var line = JsonSerializer.Serialize(result);
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
            Console.WriteLine(line);
        }
        return 0;
    }

    private static string Name(Type type, Dictionary<string, object?> parameters, MethodInfo method)
        => $"{type.Name}.{method.Name}({string.Join(",", parameters.Select(p => $"{p.Key}={p.Value}"))})";

    private static string? Option(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    private static bool IsOptionValue(string[] args, string value)
    {
        var index = Array.IndexOf(args, value);
        return index > 0 && args[index - 1].StartsWith("--", StringComparison.Ordinal);
    }

    private static IEnumerable<(Type, Dictionary<string, object?>, MethodInfo)> Cases()
    {
        foreach (var type in Types)
        {
            IEnumerable<Dictionary<string, object?>> combinations = [new()];
            foreach (var property in type.GetProperties().Where(p => p.GetCustomAttribute<ParamsAttribute>() != null))
                combinations = combinations.SelectMany(c => property.GetCustomAttribute<ParamsAttribute>()!.Values
                    .Select(v => new Dictionary<string, object?>(c) { [property.Name] = v })).ToList();
            foreach (var combination in combinations)
                foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<BenchmarkAttribute>() != null))
                    yield return (type, combination, method);
        }
    }

    private static async Task<Dictionary<string, object>> Measure(Type type, Dictionary<string, object?> parameters, MethodInfo method,
        ConnectionMultiplexer? redis, double? stacks)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var (key, value) in parameters) type.GetProperty(key)!.SetValue(instance, value);
        type.GetProperty("SelectPerformanceCores")?.SetValue(instance, false);
        await Invoke(type.GetMethods().Single(m => m.GetCustomAttribute<GlobalSetupAttribute>() != null), instance);
        var setupMethod = type.GetMethods().SingleOrDefault(m => m.GetCustomAttribute<IterationSetupAttribute>() != null);
        Action? iterationSetup = setupMethod == null ? null : () => setupMethod.Invoke(instance, null);
        var perInvoke = method.GetCustomAttribute<BenchmarkAttribute>()!.OperationsPerInvoke;
        // Cases that need a fresh container per iteration bound how many invocations one iteration may run
        var invocationCap = type.GetField("InvocationsPerIteration")?.GetValue(null) is int count ? count : int.MaxValue;
        var op = Operation(method, instance);
        try
        {
            if (stacks is { } seconds)
            {
                iterationSetup?.Invoke();
                for (var i = 0; i < Math.Min(invocationCap, 2000); i++) op();
                iterationSetup?.Invoke();
                return await AllocationSites.RunAsync(op, iterationSetup, setupMethod?.Name, perInvoke, seconds, invocationCap);
            }

            // Size batches to about 100 ms after a warmup
            iterationSetup?.Invoke();
            var probe = Stopwatch.StartNew();
            var warm = 0;
            while (probe.ElapsedMilliseconds < 300 && warm < Math.Min(invocationCap, 200_000)) { op(); warm++; }
            var batch = (int)Math.Clamp(100 / (probe.Elapsed.TotalMilliseconds / warm), 20, Math.Min(invocationCap, 200_000));
            var times = new List<double>();
            var bytes = new List<double>();
            for (var round = 0; round < 7; round++)
            {
                iterationSetup?.Invoke();
                var before = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < batch; i++) op();
                watch.Stop();
                var after = GC.GetTotalAllocatedBytes(true);
                times.Add(watch.Elapsed.TotalNanoseconds / batch / perInvoke);
                bytes.Add((after - before) / (double)batch / perInvoke);
            }

            // Commands in a separate pass: SqlClient diagnostics allocate, so they must not overlap the byte count
            var commandBatch = Math.Min(batch, 200);
            iterationSetup?.Invoke();
            var redisBefore = redis == null ? null : await RedisCalls(redis);
            using var sql = new SqlCommandCounter();
            for (var i = 0; i < commandBatch; i++) op();
            sql.Stop();
            var redisAfter = redis == null ? null : await RedisCalls(redis);
            var operations = (double)commandBatch * perInvoke;
            var result = new Dictionary<string, object>
            {
                ["bytes"] = Math.Round(Median(bytes), 1),
                ["bytesMin"] = Math.Round(bytes.Min(), 1),
                ["ns"] = Math.Round(Median(times), 1),
                ["batch"] = batch,
            };
            if (redisBefore != null)
            {
                var calls = redisAfter!.Select(p => (p.Key, PerOp: (p.Value - redisBefore.GetValueOrDefault(p.Key)) / operations))
                    .Where(p => p.PerOp >= 0.005).OrderBy(p => p.Key).ToDictionary(p => p.Key, p => Math.Round(p.PerOp, 2));
                if (calls.Count != 0) result["redis"] = calls;
            }
            if (sql.Commands != 0)
            {
                result["sqlCommands"] = Math.Round(sql.Commands / operations, 2);
                result["sqlTextChars"] = Math.Round(sql.TextChars / operations, 1);
                result["sqlParameters"] = Math.Round(sql.Parameters / operations, 1);
                result["sqlShapes"] = sql.Shapes.OrderByDescending(p => p.Value).Take(4).ToDictionary(p => p.Key, p => Math.Round(p.Value / operations, 2));
            }
            return result;
        }
        finally
        {
            var cleanup = type.GetMethods().SingleOrDefault(m => m.GetCustomAttribute<GlobalCleanupAttribute>() != null);
            if (cleanup != null) await Invoke(cleanup, instance);
        }
    }

    private static Action Operation(MethodInfo method, object instance)
    {
        var returns = method.ReturnType;
        if (returns == typeof(int)) { var f = method.CreateDelegate<Func<int>>(instance); return () => f(); }
        if (returns == typeof(Task)) { var f = method.CreateDelegate<Func<Task>>(instance); return () => f().GetAwaiter().GetResult(); }
        if (returns == typeof(Task<int>)) { var f = method.CreateDelegate<Func<Task<int>>>(instance); return () => f().GetAwaiter().GetResult(); }
        if (returns == typeof(ValueTask<int>)) { var f = method.CreateDelegate<Func<ValueTask<int>>>(instance); return () => f().GetAwaiter().GetResult(); }
        if (returns == typeof(ValueTask)) { var f = method.CreateDelegate<Func<ValueTask>>(instance); return () => f().GetAwaiter().GetResult(); }
        throw new NotSupportedException(returns.Name);
    }

    private static async Task Invoke(MethodInfo method, object instance)
    {
        var result = method.Invoke(instance, null);
        if (result is Task task) await task;
        else if (result is ValueTask valueTask) await valueTask;
    }

    private static async Task<Dictionary<string, long>> RedisCalls(ConnectionMultiplexer redis)
    {
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var calls = new Dictionary<string, long>();
        foreach (var group in await server.InfoAsync("commandstats"))
            foreach (var pair in group)
            {
                if (!pair.Key.StartsWith("cmdstat_", StringComparison.Ordinal) || pair.Key == "cmdstat_info") continue;
                var field = pair.Value.Split(',').First(part => part.StartsWith("calls=", StringComparison.Ordinal));
                calls[pair.Key["cmdstat_".Length..]] = long.Parse(field["calls=".Length..], CultureInfo.InvariantCulture);
            }
        return calls;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private sealed class SqlCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        private volatile bool _stopped;
        internal int Commands, TextChars, Parameters;
        internal Dictionary<string, int> Shapes { get; } = [];

        internal SqlCommandCounter() => _all = DiagnosticListener.AllListeners.Subscribe(this);
        internal void Stop() => _stopped = true;

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener") lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (_stopped || !e.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || e.Value == null) return;
            if (e.Value.GetType().GetProperty("Command")?.GetValue(e.Value) is not System.Data.Common.DbCommand command) return;
            lock (Shapes)
            {
                Commands++;
                TextChars += command.CommandText.Length;
                Parameters += command.Parameters.Count;
                var text = command.CommandText.Trim();
                var shape = text.Length <= 60 ? text : text[..60];
                Shapes[shape] = Shapes.GetValueOrDefault(shape) + 1;
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void Dispose()
        {
            _all.Dispose();
            lock (_subscriptions) foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
