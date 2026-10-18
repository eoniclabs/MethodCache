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
    internal IServiceCollection Services { get; }

    /// <summary>Sets defaults for cached methods.</summary>
    public MethodCacheBuilder Defaults(Action<CacheEntryOptions.Builder> configure)
    { Configuration.DefaultPolicy(configure); return this; }
    /// <summary>Sets defaults for one named method group.</summary>
    public MethodCacheBuilder Group(string name, Action<CacheEntryOptions.Builder> configure)
    { Configuration.ForGroup(name).Configure(configure); return this; }
    /// <summary>Selects the module generated in this type's assembly and roots its module initializer.</summary>
    public MethodCacheBuilder FromAssemblyOf<T>()
    {
        var assembly = typeof(T).Assembly;
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        Assemblies.Add(assembly);
        return this;
    }
    /// <summary>Selects an explicitly named generated module assembly; use FromAssemblyOf for static AOT roots.</summary>
    public MethodCacheBuilder FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        Assemblies.Add(assembly);
        return this;
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
    /// <summary>Reads and watches the application's configuration section.</summary>
    public MethodCacheBuilder FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache")
    { Services.AddMethodCacheFromConfiguration(configuration, sectionName); return this; }
    /// <summary>Selects JSON value serialization with the serializer's defaults.</summary>
    public MethodCacheBuilder UseJson()
    { Services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(OwnedServices.Key, new JsonCacheSerializer())); return this; }
    /// <summary>Selects JSON value serialization with explicit options, including an AOT resolver when needed.</summary>
    public MethodCacheBuilder UseJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Services.Replace(ServiceDescriptor.KeyedSingleton<ISerializer>(OwnedServices.Key, new JsonCacheSerializer(options)));
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
        => Configuration.ForService<TService>().Method(method);
    /// <summary>Selects a method returning an asynchronous task result.</summary>
    public IFluentMethodConfiguration<TResult> Method<TService, TResult>(Expression<Func<TService, Task<TResult>>> method)
        => Configuration.ForService<TService>().Method(method);
    /// <summary>Selects a method returning a value-task result.</summary>
    public IFluentMethodConfiguration<TResult> Method<TService, TResult>(Expression<Func<TService, ValueTask<TResult>>> method)
        => Configuration.ForService<TService>().Method(method);
    /// <summary>Selects a method returning no value.</summary>
    public IFluentMethodConfiguration Method<TService>(Expression<Action<TService>> method)
        => Configuration.ForService<TService>().Method(method);
    /// <summary>Selects a task method returning no value.</summary>
    public IFluentMethodConfiguration Method<TService>(Expression<Func<TService, Task>> method)
        => Configuration.ForService<TService>().Method(method);
}
