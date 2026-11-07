using System.CodeDom.Compiler;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Registration = Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions;

namespace MethodCache.Testing;

/// <summary>
/// Checks that every registered service with cached methods can be resolved and is wrapped by its generated caching
/// decorator, so a registration mistake fails a test instead of the application's startup or its first call.
/// </summary>
/// <example>
/// <code>
/// Assert.Empty(CachedServices.Problems(provider));
/// </code>
/// </example>
public static class CachedServices
{
    /// <summary>
    /// The problems of the services with cached methods registered in <paramref name="provider"/>: a service
    /// registered after <c>AddMethodCache</c>, which has no decorator; one that can't be resolved, such as one with a
    /// dependency the container lacks; and one that resolves to something other than its generated decorator, so its
    /// calls aren't cached. An empty list means every one of them resolves to its decorator.
    /// </summary>
    /// <remarks>
    /// Each service is resolved in a scope of its own, which is disposed afterwards. A service registered with an open
    /// generic type, such as <c>typeof(IRepository&lt;&gt;)</c>, can't be resolved without its type arguments and is
    /// skipped. When a service is registered after <c>AddMethodCache</c>, only those registrations are reported,
    /// since resolving any cached service fails until they are fixed.
    /// </remarks>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <returns>Each problem, with the service it is about.</returns>
    /// <exception cref="InvalidOperationException"><c>AddMethodCache</c> wasn't called on the provider's services.</exception>
    public static IReadOnlyList<CachedServiceProblem> Problems(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var validation = provider.GetService<Registration.RegistrationValidation>()
            ?? throw new InvalidOperationException("The provider has no MethodCache services. Call services.AddMethodCache() before building it.");

        var registrations = validation.CachedRegistrations().ToArray();
        var registeredAfter = registrations.Where(descriptor => !validation.Accepted.Contains(descriptor))
            .Select(static descriptor => new CachedServiceProblem(descriptor.ServiceType, descriptor.ServiceKey, Registration.RegistrationValidation.RegisteredAfter(descriptor.ServiceType)))
            .ToList();
        if (registeredAfter.Count > 0) return registeredAfter;

        var problems = new List<CachedServiceProblem>();
        var services = registrations
            .Where(static descriptor => !descriptor.ServiceType.IsGenericTypeDefinition && !Equals(descriptor.ServiceKey, KeyedService.AnyKey))
            .Select(static descriptor => (descriptor.ServiceType, descriptor.ServiceKey))
            .Distinct();
        foreach (var (serviceType, serviceKey) in services)
        {
            using var scope = provider.CreateScope();
            object?[] instances;
            try
            {
                instances = (serviceKey == null
                    ? scope.ServiceProvider.GetServices(serviceType)
                    : scope.ServiceProvider.GetKeyedServices(serviceType, serviceKey)).ToArray();
            }
            catch (Exception error)
            {
                problems.Add(new CachedServiceProblem(serviceType, serviceKey, $"Resolving it throws {error.GetType().Name}: {error.Message}"));
                continue;
            }

            foreach (var instance in instances)
            {
                if (instance == null || IsDecorator(instance.GetType())) continue;
                problems.Add(new CachedServiceProblem(serviceType, serviceKey,
                    $"It resolves to {instance.GetType()}, not its generated caching decorator, so its calls aren't cached. Register it with " +
                    "AddSingleton, AddScoped or AddTransient before AddMethodCache, in the project where MethodCache's generator runs."));
            }
        }

        return problems;
    }

    private static bool IsDecorator(Type type)
        => type.GetCustomAttribute<GeneratedCodeAttribute>() is { Tool: "MethodCacheGenerator" };
}

/// <summary>A problem with a service that has cached methods, as <see cref="CachedServices.Problems"/> reports it.</summary>
/// <param name="Service">The service type, such as the interface with <c>[Cache]</c> methods.</param>
/// <param name="ServiceKey">The key of a keyed registration, or <c>null</c>.</param>
/// <param name="Problem">What is wrong, and how to fix it.</param>
public sealed record CachedServiceProblem(Type Service, object? ServiceKey, string Problem)
{
    /// <summary>The service, its key when it has one, and the problem.</summary>
    public override string ToString()
        => ServiceKey == null ? $"{Service}: {Problem}" : $"{Service} (key {ServiceKey}): {Problem}";
}
