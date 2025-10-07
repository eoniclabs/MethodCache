using System.Linq;
using System.Collections.Generic;
using System.IO;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Diagnostics;
using MethodCache.SampleApp.Configuration;
using MethodCache.SampleApp.Infrastructure;
using MethodCache.SampleApp.Runner;
using MethodCache.SampleApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var yamlPath = Path.Combine(AppContext.BaseDirectory, "methodcache.yaml");

// Register MethodCache with every configuration surface so we can demo precedence
builder.Services.AddMethodCacheWithSources(cache =>
{
    cache.AddAttributeSource(typeof(ICacheConfigurationShowcase).Assembly);
    cache.AddJsonConfiguration(builder.Configuration);
    if (File.Exists(yamlPath))
    {
        cache.AddYamlConfiguration(yamlPath);
    }
    cache.AddProgrammaticConfiguration(programmatic =>
    {
        programmatic.AddMethod(
            CacheConfigurationMetadata.ServiceType,
            nameof(ICacheConfigurationShowcase.GetPriceAsync),
            settings =>
            {
                settings.Duration = TimeSpan.FromMinutes(12);
                settings.Tags = new List<string> { "fluent", "pricing" };
                settings.IsIdempotent = true;
            });
    });
});

// Use enhanced metrics so we can print cache activity summaries
builder.Services.AddSingleton<ICacheMetricsProvider, EnhancedMetricsProvider>();

// Sample services and runner orchestrating the scenarios
builder.Services.AddSingleton<ICacheShowcaseService, ConfigDrivenCacheService>();
builder.Services.AddSingleton<SampleScenarioRunner>();

var host = builder.Build();
var runner = host.Services.GetRequiredService<SampleScenarioRunner>();
var diagnostics = host.Services.GetRequiredService<PolicyDiagnosticsService>();

Console.WriteLine();
Console.WriteLine("Effective MethodCache policies available at startup:");
foreach (var report in diagnostics.GetAllPolicies().OrderBy(r => r.MethodId))
{
    var duration = report.Policy.Duration?.ToString() ?? "(default)";
    var sources = string.Join(", ", report.Contributions.Select(c => c.SourceId).Distinct());
    Console.WriteLine($" • {report.MethodId}: Duration={duration}, Sources={sources}");
}
Console.WriteLine();

try
{
    await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
}
catch (TimeoutException)
{
    Console.WriteLine("⚠️  Sample execution exceeded the 30s timeout and was aborted to prevent hanging.");
}
