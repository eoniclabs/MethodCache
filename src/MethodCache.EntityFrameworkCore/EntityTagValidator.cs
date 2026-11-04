using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.EntityFrameworkCore;

/// <summary>Checks the entity tags against the model when the host starts, so a typo fails at once.</summary>
internal sealed class EntityTagValidator<TContext>(IServiceProvider services, EntityInvalidation<TContext> registration) : IHostedService
    where TContext : DbContext
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (registration.Options.Entities.Count == 0) return;

        var scope = services.CreateAsyncScope();
        await using var _ = scope.ConfigureAwait(false);
        TContext? context = null;
        var created = false;
        IModel model;
        try
        {
            if (scope.ServiceProvider.GetService<IDbContextFactory<TContext>>() is { } factory)
            {
                context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                created = true;
            }
            else
            {
                context = scope.ServiceProvider.GetService<TContext>();
            }
            if (context == null) return;
            model = context.Model;
        }
        catch (Exception)
        {
            // The context needs something only a request has (a tenant, a user, a connection string): the first save
            // checks the tags instead
            if (created) await context!.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            registration.GetMap(model);
        }
        finally
        {
            if (created) await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
