using System.Linq;
using MethodCache.Core;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.PolicyPipeline.Diagnostics;
using MethodCache.SampleApp.Configuration;
using MethodCache.SampleApp.Infrastructure;
using MethodCache.SampleApp.Runner;
using MethodCache.SampleApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMethodCache(config =>
{
    config.DefaultPolicy(options => options.WithDuration(TimeSpan.FromMinutes(5)));

    config.ForService<ICacheConfigurationShowcase>()
        .Method(service => service.GetPriceAsync(default!))
        .Configure(options =>
        {
            options.WithDuration(TimeSpan.FromMinutes(12))
                   .WithTags("fluent", "pricing");
        });
}, typeof(ICacheConfigurationShowcase).Assembly);

builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);

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
