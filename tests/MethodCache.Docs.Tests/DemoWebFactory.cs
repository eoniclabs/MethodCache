using MethodCache.WebDemo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Docs.Tests;

internal sealed class DemoWebFactory : WebApplicationFactory<ProductCatalog>
{
    private readonly string _environment;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"methodcache-web-test-{Guid.NewGuid():N}");
    private readonly Action<IServiceCollection>? _configure;
    private readonly int _queryDelayMs;
    public FakeTimeProvider Clock { get; }
    public string DataFile { get; }

    public DemoWebFactory(string environment = "Development", string? dataFile = null, FakeTimeProvider? clock = null, Action<IServiceCollection>? configure = null, int queryDelayMs = 0)
    {
        _environment = environment;
        DataFile = dataFile ?? Path.Combine(_directory, "products.sqlite");
        Clock = clock ?? new FakeTimeProvider(DateTimeOffset.UtcNow);
        _configure = configure;
        _queryDelayMs = queryDelayMs;
    }

    public static string RepositoryRoot
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "MethodCache.sln"))) root = root.Parent;
            return root?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseContentRoot(Path.Combine(RepositoryRoot, "samples", "MethodCache.WebDemo"))
            .UseEnvironment(_environment)
            .UseSetting("Demo:DataFile", DataFile)
            .UseSetting("Demo:QueryDelayMs", _queryDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("ConnectionStrings:Redis", "")
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            _configure?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
