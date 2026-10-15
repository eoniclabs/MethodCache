using System.Diagnostics;
using System.Reflection;
using MethodCache;

// Each worker starts a new CLR and therefore gets an independently generated process hash secret.
// Only public installed package APIs are used. Neither a seed nor an internal hash is read or exported.
internal static class SeedIsolationCheck
{
    internal static async Task<string?> RunAsync(Backend backend)
    {
        var isolation = "seeds" + Guid.NewGuid().ToString("N")[..12];
        foreach (var phase in new[] { "origin", "hit", "key", "origin", "tag", "origin", "clear", "origin", "hit" })
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process executable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--seed-isolation");
            start.ArgumentList.Add(backend.ToString());
            start.ArgumentList.Add(isolation);
            start.ArgumentList.Add(phase);
            using var worker = Process.Start(start)!;
            var output = worker.StandardOutput.ReadToEndAsync();
            var error = worker.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await worker.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                return $"{backend} process phase {phase} exceeded its infrastructure deadline";
            }
            _ = await output;
            _ = await error;
            if (worker.ExitCode != 0) return $"{backend} independent-process phase {phase} failed ({worker.ExitCode})";
        }
        return null;
    }

    internal static async Task<int> WorkerAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length != 4 || !Enum.TryParse<Backend>(arguments[1], out var backend)) return 2;
            var phase = arguments[3];
            var variable = backend == Backend.Redis ? "METHODCACHE_REDIS_URL" : "METHODCACHE_SQLSERVER_CONNECTION";
            var connection = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(connection)) return 3;
            await using var instance = DistributedCheck.CreateInstance(backend, connection, arguments[2], messagePack: false);
            await instance.StartAsync();
            // Establish both storage and subscription before testing sharing, independently of connection startup.
            var health = instance.Services.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await health.CheckHealthAsync(limit.Token)).Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
            {
                limit.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            UserService.Reset();
            var key = MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetAsync), MethodCacheKeys.Part(7));
            if (phase is "origin" or "hit")
            {
                var result = await instance.Users.GetAsync(7);
                if (result != new User(7, "user-7") || UserService.Calls != (phase == "origin" ? 1 : 0)) return 4;
                var entries = instance.Administrator.ListEntries();
                if (!entries.Any(entry => entry.Key == key)) return 5;
            }
            else if (phase == "key")
            {
                var result = await instance.Services.GetRequiredService<ICacheManager>().InvalidateKeysAsync([key]);
                if (result.Distributed != 1 || result.Queued != 0) return 6;
            }
            else if (phase == "tag")
            {
                var result = await instance.Services.GetRequiredService<ICacheManager>().InvalidateTagsAsync(["user:7"]);
                if (result.Distributed != 1 || result.Queued != 0) return 7;
            }
            else if (phase == "clear") await instance.Administrator.ClearAsync();
            else return 8;
            return 0;
        }
        catch (Exception error)
        {
            // Backend messages can contain credentials; worker diagnostics deliberately export only the type.
            Console.Error.WriteLine(error.GetType().FullName);
            return 10;
        }
    }
}
