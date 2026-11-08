using System.Reflection;
using System.Linq.Expressions;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Extensibility;
using System.Text.Json;
using System.Text.Json.Serialization;
using MethodCache.Infrastructure;

namespace MethodCache;

/// <summary>Configures startup policy, generated modules and explicit registration behavior.</summary>
public sealed class MethodCacheBuilder
{
    internal MethodCacheBuilder(IServiceCollection services) => Services = services;
    internal FluentMethodCacheConfiguration Configuration { get; } = new();
    internal List<Assembly> Assemblies { get; } = [];
    internal ServiceLifetime? AutoLifetime { get; private set; }
    internal bool ConfiguresDistributed { get; private set; }

    /// <summary>
    /// The service collection <c>AddMethodCache</c> was called on. An extension method such as
    /// <c>mc.UseRedis(...)</c> registers its services here; use it to write your own <c>mc.UseXxx()</c> extension
    /// that adds services MethodCache resolves, and return the builder so calls chain.
    /// </summary>
    /// <example>
    /// <code>
    /// public static MethodCacheBuilder UseClock(this MethodCacheBuilder builder, TimeProvider clock)
    /// {
    ///     builder.Services.AddSingleton(clock);
    ///     return builder;
    /// }
    /// </code>
    /// </example>
    public IServiceCollection Services { get; }

    /// <summary>Sets defaults for cached methods.</summary>
    public MethodCacheBuilder Defaults(Action<CacheEntryOptions.Builder> configure)
    { Configuration.DefaultPolicy(configure); return this; }
    /// <summary>Sets defaults for one named method group.</summary>
    public MethodCacheBuilder Group(string name, Action<CacheEntryOptions.Builder> configure)
    { Configuration.ForGroup(name).Configure(configure); return this; }
    /// <summary>
    /// Adds the module generated in this type's assembly, also under Native AOT, and lets it win when several modules
    /// generate the same interface. Services the other modules generate stay cached. With <see cref="AutoRegister"/>,
    /// only the selected modules register missing services.
    /// </summary>
    public MethodCacheBuilder FromAssemblyOf<T>()
    {
        var assembly = typeof(T).Assembly;
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        Assemblies.Add(assembly);
        return this;
    }
    /// <summary>
    /// Adds the module generated in <paramref name="assembly"/>, as <see cref="FromAssemblyOf{T}"/> does; prefer
    /// <see cref="FromAssemblyOf{T}"/>, which also roots the assembly for Native AOT.
    /// </summary>
    public MethodCacheBuilder FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        Assemblies.Add(assembly);
        return this;
    }
    // Loads the module generated for the service, also under Native AOT, so a policy for a [Cache] method whose service
    // this container doesn't register is known and skipped rather than taken for a misspelled method
    private FluentMethodCacheConfiguration ServiceModule<TService>()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(TService).Assembly.ManifestModule.ModuleHandle);
        return Configuration;
    }
    /// <summary>Opts into registration of missing services with the stated lifetime and exactly one candidate.</summary>
    public MethodCacheBuilder AutoRegister(ServiceLifetime lifetime)
    {
        if (!Enum.IsDefined(lifetime)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        AutoLifetime = lifetime;
        return this;
    }
    /// <summary>Configures the method-result memory engine.</summary>
    public MethodCacheBuilder Memory(Action<MethodCacheMemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }
    /// <summary>
    /// Configures the distributed cache added with <c>UseRedis</c>, <c>UseSqlServer</c> or <c>UsePostgreSql</c>: key prefix, timeouts,
    /// tag checks and the round-trip check.
    /// </summary>
    public MethodCacheBuilder Distributed(Action<DistributedCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfiguresDistributed = true;
        Services.Configure(configure);
        return this;
    }
    /// <summary>Configures what spans record: cache keys and tags are off by default.</summary>
    public MethodCacheBuilder Telemetry(Action<MethodCacheTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }
    /// <summary>Reads and watches the application's configuration section.</summary>
    public MethodCacheBuilder FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    { Services.AddMethodCacheFromConfiguration(configuration, sectionName); return this; }
    /// <summary>Selects JSON value serialization with the serializer's defaults.</summary>
    public MethodCacheBuilder UseJson()
    { Services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(OwnedServices.Key, static (sp, _) => new JsonCacheSerializer(null, sp.GetService<DerivedTypeRegistry>()))); return this; }
    /// <summary>Selects JSON value serialization with explicit options, including an AOT resolver when needed.</summary>
    public MethodCacheBuilder UseJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Copied now, so later changes to the options don't apply
        var copy = new JsonSerializerOptions(options);
        Services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(OwnedServices.Key, (sp, _) => new JsonCacheSerializer(copy, sp.GetService<DerivedTypeRegistry>())));
        return this;
    }
    /// <summary>Selects JSON value serialization using a source-generated context, including under Native AOT.</summary>
    public MethodCacheBuilder UseJson(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UseJson(context.Options);
    }
    /// <summary>Selects a method returning a synchronous result.</summary>
    public IFluentMethodConfiguration<TResult> Method<TService, TResult>(Expression<Func<TService, TResult>> method)
        => ServiceModule<TService>().ForService<TService>().Method(method);
    /// <summary>Selects a method returning an asynchronous task result.</summary>
    public IFluentMethodConfiguration<TResult> Method<TService, TResult>(Expression<Func<TService, Task<TResult>>> method)
        => ServiceModule<TService>().ForService<TService>().Method(method);
    /// <summary>Selects a method returning a value-task result.</summary>
    public IFluentMethodConfiguration<TResult> Method<TService, TResult>(Expression<Func<TService, ValueTask<TResult>>> method)
        => ServiceModule<TService>().ForService<TService>().Method(method);
}
