using System.CodeDom.Compiler;
using System.Reflection;
using MethodCache.Generated;
using Microsoft.Extensions.DependencyInjection;
using Registration = Microsoft.Extensions.DependencyInjection.MethodCacheServiceCollectionExtensions;

namespace MethodCache.Testing;

/// <summary>
/// Checks that every registered service with cached methods can be resolved and is wrapped by its generated caching
/// decorator, so a registration mistake fails a test instead of the application's startup or its first call.
/// </summary>
/// <example>
/// <code>
/// Assert.Empty(await CachedServices.ProblemsAsync(provider));
/// // Or, when the services need a database or a key vault to resolve:
/// Assert.Empty(CachedServices.RegistrationProblems(provider));
/// </code>
/// </example>
public static class CachedServices
{
    private const string NoCachedServices =
        "The provider has no services with cached methods, so nothing was checked. Pass the application's provider, built after its services are registered.";

    /// <summary>
    /// The registration problems of the services with cached methods registered in <paramref name="provider"/>,
    /// found without resolving any service: a service registered after <c>AddMethodCache</c>, which has no decorator.
    /// An empty list means every one of them is registered before <c>AddMethodCache</c>, so it gets its decorator.
    /// </summary>
    /// <remarks>
    /// Use it when resolving the services needs infrastructure a test doesn't have, such as a database or a key vault.
    /// <see cref="ProblemsAsync"/> also resolves each service, so it finds a dependency the container lacks too.
    /// </remarks>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <returns>Each problem, with the service it is about.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddMethodCache</c> wasn't called on the provider's services, or no service with cached methods is registered.
    /// </exception>
    public static IReadOnlyList<CachedServiceProblem> RegistrationProblems(IServiceProvider provider)
    {
        var validation = Validation(provider);
        return RegisteredAfter(validation, Registrations(validation));
    }

    /// <summary>
    /// The services with cached methods that <paramref name="provider"/> registers, each once, ordered by full name:
    /// the services <see cref="ProblemsAsync"/> and <see cref="RegistrationProblems"/> check. Compare it with the
    /// services the application should have, so a test also fails for a service that is never registered, which
    /// neither check can see.
    /// </summary>
    /// <example>
    /// <code>
    /// Assert.Equal([typeof(IOrderService), typeof(IPriceService)], CachedServices.Registered(provider));
    /// </code>
    /// </example>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <returns>The service types, such as the interfaces with <c>[Cache]</c> methods; a keyed service is listed once.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddMethodCache</c> wasn't called on the provider's services, or no service with cached methods is registered.
    /// </exception>
    public static IReadOnlyList<Type> Registered(IServiceProvider provider)
        => Registrations(Validation(provider))
            .Select(static descriptor => descriptor.ServiceType)
            .Distinct()
            .OrderBy(static type => type.FullName ?? type.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The services with cached methods that <paramref name="assemblies"/> declare, each once, ordered by full name as
    /// <see cref="Registered"/> orders them: the interfaces MethodCache's generator made a decorator for in those
    /// projects. Compare the two, so a new cached interface is covered without changing the test, and one the
    /// application never registers fails it.
    /// </summary>
    /// <example>
    /// <code>
    /// Assert.Equal(CachedServices.Declared(typeof(IOrderService).Assembly), CachedServices.Registered(provider));
    /// </code>
    /// </example>
    /// <remarks>
    /// Pass the assemblies of the projects that declare the interfaces. A generic interface is listed as its definition,
    /// such as <c>typeof(IRepository&lt;&gt;)</c>.
    /// </remarks>
    /// <param name="assemblies">The assemblies with the cached interfaces, such as <c>typeof(IOrderService).Assembly</c>.</param>
    /// <returns>The service types, such as the interfaces with <c>[Cache]</c> methods.</returns>
    /// <exception cref="ArgumentException">No assembly is passed.</exception>
    /// <exception cref="InvalidOperationException">The assemblies declare no service with cached methods.</exception>
    public static IReadOnlyList<Type> Declared(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Length == 0) throw new ArgumentException("Pass the assemblies that declare the cached interfaces.", nameof(assemblies));
        foreach (var assembly in assemblies) ArgumentNullException.ThrowIfNull(assembly, nameof(assemblies));

        var declared = GeneratedCode.Describe(assemblies, [])
            .Where(module => assemblies.Contains(module.Module.GetType().Assembly))
            .SelectMany(static module => module.DeclaredServices)
            .Distinct()
            .OrderBy(static type => type.FullName ?? type.Name, StringComparer.Ordinal)
            .ToList();
        return declared.Count > 0
            ? declared
            : throw new InvalidOperationException(
                $"{string.Join(", ", assemblies.Select(static a => a.GetName().Name))} declare no service with cached methods, so nothing " +
                "can be compared. Pass the assemblies of the projects with the [Cache] interfaces, where MethodCache's generator runs.");
    }

    /// <summary>
    /// The problems of the services with cached methods registered in <paramref name="provider"/>: a service
    /// registered after <c>AddMethodCache</c>, which has no decorator; one that can't be resolved, such as one with a
    /// dependency the container lacks; and one that resolves to something other than its generated decorator, so its
    /// calls aren't cached. An empty list means every one of them resolves to its decorator.
    /// </summary>
    /// <remarks>
    /// Each service is resolved in a scope of its own, which is disposed asynchronously afterwards, so a service or
    /// dependency that only implements <see cref="IAsyncDisposable"/> is disposed as the application disposes it. A
    /// service registered with an open
    /// generic type, such as <c>typeof(IRepository&lt;&gt;)</c>, can't be resolved without its type arguments and is
    /// skipped. When a service is registered after <c>AddMethodCache</c>, only those registrations are reported,
    /// since resolving any cached service fails until they are fixed.
    /// </remarks>
    /// <param name="provider">The application's service provider, built as in the application.</param>
    /// <returns>Each problem, with the service it is about.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddMethodCache</c> wasn't called on the provider's services, or no service with cached methods is registered.
    /// </exception>
    public static async Task<IReadOnlyList<CachedServiceProblem>> ProblemsAsync(IServiceProvider provider)
    {
        var validation = Validation(provider);
        var registrations = Registrations(validation);
        var registeredAfter = RegisteredAfter(validation, registrations);
        if (registeredAfter.Count > 0) return registeredAfter;

        var problems = new List<CachedServiceProblem>();
        var services = registrations
            .Where(static descriptor => !descriptor.ServiceType.IsGenericTypeDefinition && !Equals(descriptor.ServiceKey, KeyedService.AnyKey))
            .Select(static descriptor => (descriptor.ServiceType, descriptor.ServiceKey))
            .Distinct();
        foreach (var (serviceType, serviceKey) in services)
        {
            var scope = provider.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
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
        }

        return problems;
    }

    private static Registration.RegistrationValidation Validation(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetService<Registration.RegistrationValidation>()
            ?? throw new InvalidOperationException("The provider has no MethodCache services. Call services.AddMethodCache() before building it.");
    }

    // An empty list would read as "no problems" although nothing was checked, such as with the wrong provider
    private static ServiceDescriptor[] Registrations(Registration.RegistrationValidation validation)
    {
        var registrations = validation.CachedRegistrations().ToArray();
        return registrations.Length > 0 ? registrations : throw new InvalidOperationException(NoCachedServices);
    }

    private static List<CachedServiceProblem> RegisteredAfter(Registration.RegistrationValidation validation, ServiceDescriptor[] registrations)
        => registrations.Where(descriptor => !validation.Accepted.Contains(descriptor))
            .Select(static descriptor => new CachedServiceProblem(descriptor.ServiceType, descriptor.ServiceKey, Registration.RegistrationValidation.RegisteredAfter(descriptor.ServiceType)))
            .ToList();

    private static bool IsDecorator(Type type)
        => type.GetCustomAttribute<GeneratedCodeAttribute>() is { Tool: "MethodCacheGenerator" };
}

/// <summary>A problem with a service that has cached methods, as <see cref="CachedServices.ProblemsAsync"/> reports it.</summary>
/// <param name="Service">The service type, such as the interface with <c>[Cache]</c> methods.</param>
/// <param name="ServiceKey">The key of a keyed registration, or <c>null</c>.</param>
/// <param name="Problem">What is wrong, and how to fix it.</param>
public sealed record CachedServiceProblem(Type Service, object? ServiceKey, string Problem)
{
    /// <summary>The service, its key when it has one, and the problem.</summary>
    public override string ToString()
        => ServiceKey == null ? $"{Service}: {Problem}" : $"{Service} (key {ServiceKey}): {Problem}";
}
