using MethodCache;
using MethodCache.Policies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Configuration
{
    public sealed record UserProfile(int Id);

    public sealed record Order(int Id);

    public interface IUserService
    {
        [Cache(Group = "user-data", Duration = "01:00:00", Tags = ["users"])]
        Task<UserProfile> GetProfileAsync(int userId);
    }

    public interface IOrdersService
    {
        [Cache(Duration = "00:10:00")]
        Task<Order> GetAsync(int id);
    }

    public static class Setup
    {
        public static void Startup(WebApplicationBuilder builder)
        {
            // begin-snippet: configuration-startup
            builder.Services.AddMethodCache(config =>
            {
                // Every cached method: fills in what the method leaves unset
                config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(15)));

                // Methods in the "user-data" group: [Cache(Group = "user-data")] or WithGroup("user-data")
                config.Group("user-data", o => o.WithSlidingExpiration(TimeSpan.FromMinutes(10)));

                // One method
                config.Method((IUserService s) => s.GetProfileAsync(Any<int>.Value))
                      .Configure(o => o.WithDuration(TimeSpan.FromHours(2)).RefreshAhead(TimeSpan.FromMinutes(5)));
            });
            // end-snippet
        }

        public static void FromConfiguration(WebApplicationBuilder builder)
        {
            // begin-snippet: configuration-appsettings
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);   // the "MethodCache" section
            // end-snippet
        }

        public static async Task RuntimeOverrides(WebApplication app)
        {
            // begin-snippet: configuration-runtime-overrides
            var configurator = app.Services.GetRequiredService<IRuntimeCacheConfigurator>();

            // One method, strongly typed, with who and why for the audit trail
            await configurator.UpsertAsync<IOrdersService, Order>(
                s => s.GetAsync(Any<int>.Value),
                o => o.WithDuration(TimeSpan.FromMinutes(2)),
                new RuntimeOverrideMetadata(Owner: "ops", Reason: "Incident 4211", ExpiresAt: DateTimeOffset.UtcNow.AddHours(4)));

            // By method id, for example from an admin form
            await configurator.UpsertAsync("Docs.Configuration.IUserService.GetProfileAsync",
                o => o.WithDuration(TimeSpan.FromMinutes(10)));

            // Back to the lower layers
            await configurator.RemoveAsync<IOrdersService, Order>(s => s.GetAsync(Any<int>.Value));
            await configurator.ClearAsync();
            // end-snippet
        }

        public static void Diagnostics(WebApplication app)
        {
            // begin-snippet: configuration-diagnostics
            var diagnostics = app.Services.GetRequiredService<ICacheAdministrator>();

            foreach (var report in diagnostics.GetMethods())
            {
                var sources = string.Join(", ", report.Sources);
                Console.WriteLine($"{report.MethodId}: Duration={report.Duration}, from {sources}");
            }
            // end-snippet
        }
    }
}
