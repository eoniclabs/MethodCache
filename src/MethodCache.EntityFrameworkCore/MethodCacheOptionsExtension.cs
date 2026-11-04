using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.EntityFrameworkCore;

/// <summary>Adds the per-context invalidation state to the context's own services.</summary>
internal sealed class MethodCacheOptionsExtension : IDbContextOptionsExtension
{
    public static readonly MethodCacheOptionsExtension Instance = new();

    private MethodCacheOptionsExtension() => Info = new ExtensionInfo(this);

    public DbContextOptionsExtensionInfo Info { get; }

    public void ApplyServices(IServiceCollection services)
    {
        services.AddScoped<ContextInvalidations>();
        services.AddScoped<IResettableService>(static sp => sp.GetRequiredService<ContextInvalidations>());
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "MethodCache ";

        // Stateless: every context with MethodCache shares one set of EF services
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["MethodCache"] = "1";
    }
}
