using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MethodCache;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

if (args is ["--seed-isolation", ..]) return await SeedIsolationCheck.WorkerAsync(args);

// Each check runs against the installed packages and returns what went wrong; the run fails if any check fails.
var checks = new (string Name, Func<Task<string?>> Run)[]
{
    ("Redis, two instances, JSON", () => DistributedCheck.RunAsync(Backend.Redis, messagePack: false)),
    ("Redis, two instances, MessagePack", () => DistributedCheck.RunAsync(Backend.Redis, messagePack: true)),
    ("SQL Server, two instances, JSON", () => DistributedCheck.RunAsync(Backend.SqlServer, messagePack: false)),
    ("Redis across independent processes and hash seeds", () => SeedIsolationCheck.RunAsync(Backend.Redis)),
    ("SQL Server across independent processes and hash seeds", () => SeedIsolationCheck.RunAsync(Backend.SqlServer)),
    ("ASP.NET Core admin endpoints and health check", AspNetCoreCheck.RunAsync),
    ("OpenTelemetry metrics and spans", OpenTelemetryCheck.RunAsync),
    ("HttpClient caching", HttpCachingCheck.RunAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    string? failure;
    try
    {
        failure = await run();
    }
    catch (Exception ex)
    {
        failure = ex.ToString();
    }

    Console.WriteLine(failure is null ? $"PASS {name}" : $"FAIL {name}: {failure}");
    failed += failure is null ? 0 : 1;
}

Console.WriteLine(failed == 0 ? "Add-on package smoke test passed" : $"Add-on package smoke test failed ({failed} of {checks.Length})");
return failed == 0 ? 0 : 1;

public record User(int Id, string Name);

public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = new[] { "user:{id}" })]
    Task<User> GetAsync(int id);

    [CacheInvalidate(Tags = new[] { "user:{id}" })]
    Task RenameAsync(int id, string name);
}

public sealed class UserService : IUserService
{
    private static int _calls;

    public static int Calls => Volatile.Read(ref _calls);

    public static void Reset() => Volatile.Write(ref _calls, 0);

    public Task<User> GetAsync(int id)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new User(id, "user-" + id));
    }

    public Task RenameAsync(int id, string name) => Task.CompletedTask;
}

public enum Backend { Redis, SqlServer }

/// <summary>
/// Two instances share one distributed cache: the second reads the first one's result, and an invalidation on the
/// first reaches the second one's in-memory cache.
/// </summary>
public static class DistributedCheck
{
    public static async Task<string?> RunAsync(Backend backend, bool messagePack)
    {
        var variable = backend == Backend.Redis ? "METHODCACHE_REDIS_URL" : "METHODCACHE_SQLSERVER_CONNECTION";
        var connection = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(connection))
        {
            return $"{variable} is not set";
        }

        // A fresh prefix or schema, so runs never see each other's entries
        var isolation = "smoke" + Guid.NewGuid().ToString("N")[..8];
        await using var first = CreateInstance(backend, connection, isolation, messagePack);
        await using var second = CreateInstance(backend, connection, isolation, messagePack);
        await first.StartAsync();
        await second.StartAsync();

        // Opens each instance's connection first: a write that has to wait for the very first connect can exceed the
        // L2 operation timeout and is then skipped, which is by design but would make the checks below flaky
        await first.Users.GetAsync(0);
        await second.Users.GetAsync(0);
        UserService.Reset();

        var expected = new User(1, "user-1");
        var fromFirst = await first.Users.GetAsync(1);
        var fromSecond = await second.Users.GetAsync(1);
        if (fromFirst != expected || fromSecond != expected)
        {
            return $"expected {expected}, got {fromFirst} and {fromSecond}";
        }

        if (UserService.Calls != 1)
        {
            return $"the second instance did not read the first one's result ({UserService.Calls} calls)";
        }

        await first.Users.RenameAsync(1, "renamed");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (UserService.Calls == 1 && DateTime.UtcNow < deadline)
        {
            await second.Users.GetAsync(1);
            await Task.Delay(100);
        }

        return UserService.Calls == 2 ? null : "the invalidation on the first instance did not reach the second";
    }

    internal static Instance CreateInstance(Backend backend, string connection, string isolation, bool messagePack)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IUserService, UserService>();
        services.AddMethodCache();
        services.AddHealthChecks().AddMethodCache();
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = isolation + ":");
        if (backend == Backend.Redis)
        {
            services.AddMethodCacheRedis(options =>
            {
                options.Configuration = connection;
            });
        }
        else
        {
            services.AddMethodCacheSqlServer(options =>
            {
                options.ConnectionString = connection;
                options.SchemaName = isolation;
                options.BackplanePollingInterval = TimeSpan.FromMilliseconds(200);
            });
        }

        if (messagePack)
        {
            services.AddMethodCacheMessagePack();
        }

        return new Instance(services.BuildServiceProvider());
    }

    internal sealed class Instance(ServiceProvider provider) : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope = provider.CreateAsyncScope();
        internal IServiceProvider Services => provider;

        public ICacheAdministrator Administrator => provider.GetRequiredService<ICacheAdministrator>();

        public IUserService Users => _scope.ServiceProvider.GetRequiredService<IUserService>();

        public async Task StartAsync()
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            await _scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}

/// <summary>The admin endpoints need the policy, list the cached entry and empty the cache; the health check answers.</summary>
public static class AspNetCoreCheck
{
    public static async Task<string?> RunAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddMethodCache();
        builder.Services.AddHealthChecks().AddMethodCache();
        builder.Services.AddAuthentication("Header").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Header", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMethodCacheAdmin("CacheAdmins");
        app.MapHealthChecks("/health");
        app.MapGet("/users/{id:int}", (int id, IUserService users) => users.GetAsync(id));
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        (await client.GetAsync("/users/7")).EnsureSuccessStatusCode();

        if (await client.GetAsync("/methodcache/entries") is { StatusCode: not HttpStatusCode.Unauthorized } anonymous)
        {
            return $"an anonymous caller got {(int)anonymous.StatusCode} from the admin endpoints";
        }

        if (await Send(client, HttpMethod.Get, "/methodcache/entries", "User") is { StatusCode: not HttpStatusCode.Forbidden } user)
        {
            return $"a signed-in user without the policy got {(int)user.StatusCode}";
        }

        var entries = await (await Send(client, HttpMethod.Get, "/methodcache/entries", "Admin")).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        if (!entries.Contains("IUserService.GetAsync", StringComparison.Ordinal))
        {
            return "the cached entry is not listed: " + entries;
        }

        (await Send(client, HttpMethod.Delete, "/methodcache/entries", "Admin")).EnsureSuccessStatusCode();
        var afterClear = await (await Send(client, HttpMethod.Get, "/methodcache/entries", "Admin")).Content.ReadAsStringAsync();
        if (afterClear.Contains("IUserService.GetAsync", StringComparison.Ordinal))
        {
            return "clearing the cache left the entry";
        }

        var health = await client.GetAsync("/health");
        var status = await health.Content.ReadAsStringAsync();
        return health.IsSuccessStatusCode && status == "Healthy" ? null : $"health check answered {(int)health.StatusCode} {status}";
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string role)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Role", role);
        return client.SendAsync(request);
    }

    /// <summary>Signs in whoever sends an X-Role header, with that role.</summary>
    private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Role", out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "smoke"), new Claim(ClaimTypes.Role, role.ToString()) }, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>Hits and misses reach the MethodCache meter, and a miss starts a span.</summary>
public static class OpenTelemetryCheck
{
    public static async Task<string?> RunAsync()
    {
        long hits = 0, misses = 0;
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "MethodCache")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "methodcache.requests") return;
            foreach (var tag in tags)
            {
                if (tag.Key == "result" && Equals(tag.Value, "hit")) Interlocked.Add(ref hits, value);
                if (tag.Key == "result" && Equals(tag.Value, "miss")) Interlocked.Add(ref misses, value);
            }
        });
        meters.Start();

        var spans = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("MethodCache", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => Interlocked.Increment(ref spans),
        };
        ActivitySource.AddActivityListener(activities);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IUserService, UserService>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            await users.GetAsync(3);
            await users.GetAsync(3);
            await users.GetAsync(3);
        }

        meters.RecordObservableInstruments();
        if (Interlocked.Read(ref misses) != 1 || Interlocked.Read(ref hits) != 2)
        {
            return $"expected 1 miss and 2 hits on the meter, got {misses} and {hits}";
        }

        return spans > 0 ? null : "the miss started no span";
    }
}

/// <summary>A response with max-age is served from the cache, and one with an ETag is revalidated with a 304.</summary>
public static class HttpCachingCheck
{
    public static async Task<string?> RunAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var origin = builder.Build();
        var fresh = 0;
        var tagged = 0;
        var notModified = 0;
        origin.MapGet("/fresh", (HttpContext context) =>
        {
            Interlocked.Increment(ref fresh);
            context.Response.Headers.CacheControl = "max-age=60";
            return "fresh";
        });
        origin.MapGet("/tagged", (HttpContext context) =>
        {
            Interlocked.Increment(ref tagged);
            if (context.Request.Headers.IfNoneMatch == "\"v1\"")
            {
                Interlocked.Increment(ref notModified);
                return Results.StatusCode(304);
            }

            context.Response.Headers.ETag = "\"v1\"";
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Text("tagged");
        });
        await origin.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient("origin", c => c.BaseAddress = new Uri(origin.Urls.First()))
            .AddHttpCaching();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("origin");

        var bodies = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            bodies.Add(await client.GetStringAsync("/fresh"));
            bodies.Add(await client.GetStringAsync("/tagged"));
        }

        if (bodies.Distinct().Count() != 2)
        {
            return "unexpected bodies: " + string.Join(", ", bodies);
        }

        if (fresh != 1)
        {
            return $"a fresh response reached the origin {fresh} times";
        }

        return tagged == 3 && notModified == 2 ? null : $"revalidation: {tagged} requests, {notModified} answered 304";
    }
}
