using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Every way an application registers a cached interface resolves the same with and without
/// <c>AddMethodCache()</c>, apart from caching: which implementation answers each resolution path, what it returns
/// for distinct calls, which instances are shared, and what is disposed when a scope or the provider ends.
/// </summary>
/// <remarks>
/// Each implementation returns a value that depends only on itself and the argument, so caching never changes a
/// result unless two registrations share cache entries.
/// </remarks>
public partial class DependencyInjectionEquivalenceTests
{
    public static TheoryData<string> Shapes()
    {
        var data = new TheoryData<string>();
        foreach (var name in Catalogue.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Resolve_EveryRegistrationShape_BehavesAsWithoutMethodCache(string shape)
    {
        var without = await RunAsync(Catalogue[shape], methodCache: false);
        var with = await RunAsync(Catalogue[shape], methodCache: true);

        Assert.Equal(string.Join("\n", without.Trace), string.Join("\n", with.Trace));
        Assert.Equal(0, without.Decorated);
        Assert.True(with.Decorated > 0, "MethodCache decorated nothing, so the comparison proves nothing");
    }

    private sealed record Shape(Action<IServiceCollection, Recorder> Register, bool Unkeyed, params object[] Keys);

    private static readonly Dictionary<string, Shape> Catalogue = BuildCatalogue();

    private static Dictionary<string, Shape> BuildCatalogue()
    {
        var shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);
        foreach (var lifetime in new[] { ServiceLifetime.Singleton, ServiceLifetime.Scoped, ServiceLifetime.Transient })
        {
            var l = lifetime;
            shapes[$"Single-{l}"] = new((s, _) => s.Add(Describe<PricesA>(l)), Unkeyed: true);
            shapes[$"TwoTypes-{l}"] = new((s, _) => { s.Add(Describe<PricesA>(l)); s.Add(Describe<PricesB>(l)); }, Unkeyed: true);
            shapes[$"TwoFactories-{l}"] = new((s, r) =>
            {
                s.Add(ServiceDescriptor.Describe(typeof(IEquivalencePrices), _ => new PricesA(r), l));
                s.Add(ServiceDescriptor.Describe(typeof(IEquivalencePrices), _ => new PricesB(r), l));
            }, Unkeyed: true);
            // The pattern tests use: the real registration, then a fake registered after it
            shapes[$"TypeThenFactoryFake-{l}"] = new((s, r) =>
            {
                s.Add(Describe<PricesA>(l));
                s.Add(ServiceDescriptor.Describe(typeof(IEquivalencePrices), _ => new PricesB(r), l));
            }, Unkeyed: true);
            shapes[$"Disposables-{l}"] = new((s, _) =>
            {
                s.Add(Describe<DisposablePrices>(l));
                s.Add(Describe<AsyncDisposablePrices>(l));
                s.Add(Describe<BothDisposablePrices>(l));
            }, Unkeyed: true);
            shapes[$"Keyed-{l}"] = new((s, _) =>
            {
                s.Add(DescribeKeyed<PricesA>("eu", l));
                s.Add(DescribeKeyed<PricesB>("us", l));
            }, Unkeyed: false, "eu", "us");
            shapes[$"KeyedSameKey-{l}"] = new((s, _) =>
            {
                s.Add(DescribeKeyed<PricesA>("eu", l));
                s.Add(DescribeKeyed<PricesB>("eu", l));
            }, Unkeyed: false, "eu");
            shapes[$"KeyedFactory-{l}"] = new((s, r) =>
                s.Add(ServiceDescriptor.DescribeKeyed(typeof(IEquivalencePrices), "eu", (_, key) => new KeyedPrices((string)key!, r), l)),
                Unkeyed: false, "eu");
            shapes[$"ServiceKey-{l}"] = new((s, _) =>
            {
                s.Add(DescribeKeyed<KeyedPrices>("eu", l));
                s.Add(DescribeKeyed<KeyedPrices>("us", l));
            }, Unkeyed: false, "eu", "us");
            shapes[$"ServiceKeyObject-{l}"] = new((s, _) => s.Add(DescribeKeyed<ObjectKeyedPrices>(7, l)), Unkeyed: false, 7);
            shapes[$"ServiceKeyDisposables-{l}"] = new((s, _) =>
            {
                s.Add(DescribeKeyed<KeyedDisposablePrices>("eu", l));
                s.Add(DescribeKeyed<KeyedAsyncDisposablePrices>("us", l));
            }, Unkeyed: false, "eu", "us");
            shapes[$"ServiceKeyConstructors-{l}"] = new((s, _) => s.Add(DescribeKeyed<KeyedConstructorsPrices>("eu", l)), Unkeyed: false, "eu");
            shapes[$"UnkeyedServiceKeyDefault-{l}"] = new((s, _) => s.Add(Describe<DefaultKeyPrices>(l)), Unkeyed: true);
        }

        shapes["Instances"] = new((s, r) =>
        {
            s.AddSingleton<IEquivalencePrices>(new PricesA(r));
            s.AddSingleton<IEquivalencePrices>(new DisposablePrices(r));
        }, Unkeyed: true);
        shapes["KeyedInstances"] = new((s, r) =>
        {
            s.AddKeyedSingleton<IEquivalencePrices>("eu", new PricesA(r));
            s.AddKeyedSingleton<IEquivalencePrices>("eu", new PricesB(r));
            s.AddKeyedSingleton<IEquivalencePrices>("us", new DisposablePrices(r));
        }, Unkeyed: false, "eu", "us");
        shapes["MixedLifetimes"] = new((s, _) =>
        {
            s.Add(Describe<PricesA>(ServiceLifetime.Singleton));
            s.Add(Describe<PricesB>(ServiceLifetime.Scoped));
            s.Add(Describe<DisposablePrices>(ServiceLifetime.Transient));
        }, Unkeyed: true);
        shapes["KeyedNextToUnkeyed"] = new((s, _) =>
        {
            s.Add(Describe<PricesA>(ServiceLifetime.Singleton));
            s.Add(DescribeKeyed<PricesB>("eu", ServiceLifetime.Scoped));
            s.Add(DescribeKeyed<KeyedPrices>("us", ServiceLifetime.Transient));
        }, Unkeyed: true, "eu", "us");
        return shapes;
    }

    private static ServiceDescriptor Describe<T>(ServiceLifetime lifetime) where T : IEquivalencePrices
        => ServiceDescriptor.Describe(typeof(IEquivalencePrices), typeof(T), lifetime);

    private static ServiceDescriptor DescribeKeyed<T>(object key, ServiceLifetime lifetime) where T : IEquivalencePrices
        => ServiceDescriptor.DescribeKeyed(typeof(IEquivalencePrices), key, typeof(T), lifetime);

    private sealed record Run(List<string> Trace, int Decorated);

    private static async Task<Run> RunAsync(Shape shape, bool methodCache)
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        shape.Register(services, recorder);
        var trace = new List<string>();
        var decorated = 0;
        try
        {
            if (methodCache) services.AddMethodCache();
        }
        catch (Exception error)
        {
            trace.Add("register throws " + error.GetType().Name);
            return new Run(trace, decorated);
        }

        ServiceProvider provider;
        try
        {
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        catch (Exception error)
        {
            trace.Add("build throws " + error.GetType().Name);
            return new Run(trace, decorated);
        }

        await using (provider)
        {
            var isService = provider.GetRequiredService<IServiceProviderIsKeyedService>();
            trace.Add($"is service: {isService.IsService(typeof(IEquivalencePrices))}, " +
                string.Join(", ", shape.Keys.Select(key => $"{key}={isService.IsKeyedService(typeof(IEquivalencePrices), key)}")));
            Observe("root", provider, shape, trace, ref decorated);
            for (var number = 1; number <= 2; number++)
            {
                await using (var scope = provider.CreateAsyncScope())
                {
                    Observe($"scope {number}", scope.ServiceProvider, shape, trace, ref decorated);
                }

                trace.Add($"scope {number} disposed: {recorder.TakeDisposals()}");
            }
        }

        trace.Add($"provider disposed: {recorder.TakeDisposals()}");
        return new Run(Canonical(trace), decorated);
    }

    private static void Observe(string where, IServiceProvider provider, Shape shape, List<string> trace, ref int decorated)
    {
        var count = 0;
        if (shape.Unkeyed)
        {
            trace.Add($"{where} GetService: {Resolve(() => provider.GetService<IEquivalencePrices>(), ref count)}");
            trace.Add($"{where} GetService again: {Same(() => provider.GetService<IEquivalencePrices>())}");
            trace.Add($"{where} GetServices: {ResolveMany(() => provider.GetServices<IEquivalencePrices>(), ref count)}");
        }

        foreach (var key in shape.Keys)
        {
            trace.Add($"{where} GetKeyedService({key}): {Resolve(() => provider.GetKeyedService<IEquivalencePrices>(key), ref count)}");
            trace.Add($"{where} GetKeyedService({key}) again: {Same(() => provider.GetKeyedService<IEquivalencePrices>(key))}");
            trace.Add($"{where} GetKeyedServices({key}): {ResolveMany(() => provider.GetKeyedServices<IEquivalencePrices>(key), ref count)}");
        }

        trace.Add($"{where} GetKeyedServices(AnyKey): {ResolveMany(() => provider.GetKeyedServices<IEquivalencePrices>(KeyedService.AnyKey), ref count)}");
        decorated += count;
    }

    private static string Resolve(Func<IEquivalencePrices?> resolve, ref int decorated)
    {
        try
        {
            return Describe(resolve(), ref decorated);
        }
        catch (Exception error)
        {
            return "throws " + error.GetType().Name;
        }
    }

    private static string ResolveMany(Func<IEnumerable<IEquivalencePrices>> resolve, ref int decorated)
    {
        try
        {
            var all = resolve().ToList();
            var parts = new List<string>();
            foreach (var service in all) parts.Add(Describe(service, ref decorated));
            return "[" + string.Join("; ", parts) + "]";
        }
        catch (Exception error)
        {
            return "throws " + error.GetType().Name;
        }
    }

    private static string Same(Func<IEquivalencePrices?> resolve)
    {
        try
        {
            var first = resolve();
            var second = resolve();
            return first == null ? "none" : ReferenceEquals(first, second) ? "same instance" : $"new instance {second!.Who()}";
        }
        catch (Exception error)
        {
            return "throws " + error.GetType().Name;
        }
    }

    private static string Describe(IEquivalencePrices? service, ref int decorated)
    {
        if (service == null) return "none";
        if (service.GetType().Name.Contains("Decorator", StringComparison.Ordinal)) decorated++;
        return $"{service.Who()} {service.Price(1)} {service.Price(2)} {service.Price(1)}";
    }

    // Instance numbers depend on construction order; number them by first appearance instead
    private static List<string> Canonical(List<string> trace)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        return trace.Select(line => InstanceNumber().Replace(line, match =>
        {
            if (!numbers.TryGetValue(match.Value, out var number)) numbers[match.Value] = number = numbers.Count + 1;
            return "#n" + number;
        })).ToList();
    }

    [GeneratedRegex("#[0-9]+")]
    private static partial Regex InstanceNumber();

    public interface IEquivalencePrices
    {
        [Cache] string Price(int id);

        string Who();
    }

    public sealed class Recorder
    {
        private readonly List<string> _disposals = [];
        private int _instances;

        internal int Next() => Interlocked.Increment(ref _instances);

        internal void Disposed(string who)
        {
            lock (_disposals) _disposals.Add(who);
        }

        internal string TakeDisposals()
        {
            lock (_disposals)
            {
                var text = "[" + string.Join(", ", _disposals) + "]";
                _disposals.Clear();
                return text;
            }
        }
    }

    public abstract class PricesBase(string name, Recorder recorder) : IEquivalencePrices
    {
        private readonly int _number = recorder.Next();
        protected Recorder Recorder { get; } = recorder;
        public string Price(int id) => $"{name}:{id}";
        public string Who() => $"{name}#{_number}";
    }

    public sealed class PricesA(Recorder recorder) : PricesBase("A", recorder);

    public sealed class PricesB(Recorder recorder) : PricesBase("B", recorder);

    public sealed class DisposablePrices(Recorder recorder) : PricesBase("D", recorder), IDisposable
    {
        public void Dispose() => Recorder.Disposed(Who());
    }

    public sealed class AsyncDisposablePrices(Recorder recorder) : PricesBase("AD", recorder), IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Recorder.Disposed(Who());
            return ValueTask.CompletedTask;
        }
    }

    public sealed class BothDisposablePrices(Recorder recorder) : PricesBase("BD", recorder), IDisposable, IAsyncDisposable
    {
        public void Dispose() => Recorder.Disposed(Who() + " sync");

        public ValueTask DisposeAsync()
        {
            Recorder.Disposed(Who() + " async");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class KeyedPrices([ServiceKey] string key, Recorder recorder) : PricesBase($"K({key})", recorder);

    public sealed class ObjectKeyedPrices([ServiceKey] object key, Recorder recorder) : PricesBase($"O({key})", recorder);

    public sealed class KeyedDisposablePrices([ServiceKey] string key, Recorder recorder) : PricesBase($"KD({key})", recorder), IDisposable
    {
        public void Dispose() => Recorder.Disposed(Who());
    }

    public sealed class KeyedAsyncDisposablePrices([ServiceKey] string key, Recorder recorder) : PricesBase($"KAD({key})", recorder), IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Recorder.Disposed(Who());
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The container picks the longest constructor it can satisfy: the second, since nothing registers <see cref="IMissing"/>.</summary>
    public sealed class KeyedConstructorsPrices : PricesBase
    {
        public KeyedConstructorsPrices(Recorder recorder) : base("C(none)", recorder) { }

        public KeyedConstructorsPrices([ServiceKey] string key, Recorder recorder) : base($"C({key})", recorder) { }

        public KeyedConstructorsPrices([ServiceKey] string key, Recorder recorder, IMissing missing) : base($"C({key}, {missing})", recorder) { }
    }

    public interface IMissing;

    /// <summary>Unkeyed, the container ignores <c>[ServiceKey]</c> and falls back to the default value.</summary>
    public sealed class DefaultKeyPrices([ServiceKey] string key = "none", Recorder? recorder = null) : PricesBase($"DK({key})", recorder!);
}
