using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// Creates the implementation of a type registration that MethodCache moved to a private service key, the way
    /// the container would have created it under the application's own key: a <c>[ServiceKey]</c> parameter gets the
    /// application's key, and a <c>[FromKeyedServices]</c> parameter that inherits the key resolves with it. Without
    /// this the container would hand those parameters MethodCache's private key.
    /// </summary>
    /// <remarks>
    /// The constructor is chosen as the container chooses it: the only public one, or else the one with the most
    /// parameters that can all be resolved, failing when two such constructors are ambiguous. The container still
    /// owns the instance, since it is created by a factory registration with the original lifetime.
    /// </remarks>
    internal sealed class ServiceKeyActivator
    {
        // Read by reflection: the package MethodCache compiles against predates FromKeyedServicesAttribute.LookupMode
        private static readonly PropertyInfo? LookupMode = typeof(FromKeyedServicesAttribute).GetProperty("LookupMode");

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        private readonly Type _type;
        private readonly object? _serviceKey;
        private Plan? _plan;

        internal ServiceKeyActivator([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type, object? serviceKey)
        {
            _type = type;
            _serviceKey = serviceKey;
        }

        /// <summary>Whether a constructor of <paramref name="type"/> depends on the service key it is registered with.</summary>
        internal static bool UsesServiceKey([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
            => type.GetConstructors().Any(static constructor => constructor.GetParameters().Any(static parameter =>
                parameter.IsDefined(typeof(ServiceKeyAttribute), inherit: true) ||
                parameter.GetCustomAttribute<FromKeyedServicesAttribute>(inherit: true) is { } keyed && InheritsKey(keyed)));

        internal object Create(IServiceProvider provider)
        {
            var services = provider.GetRequiredService<IServiceProviderIsService>();

            // The constructor depends only on the container's registrations, so it is chosen once per container
            var plan = Volatile.Read(ref _plan);
            if (plan == null || !ReferenceEquals(plan.Container, services))
            {
                Volatile.Write(ref _plan, plan = Choose(services));
            }

            var arguments = new object?[plan.Parameters.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = Resolve(provider, plan.Parameters[index], plan.Resolvable[index]);
            }

            return plan.Constructor.Invoke(arguments);
        }

        private Plan Choose(IServiceProviderIsService services)
        {
            var constructors = _type.GetConstructors();
            if (constructors.Length == 0)
            {
                throw new InvalidOperationException($"A suitable constructor for type '{_type}' could not be located. Ensure the type is concrete and services are registered for all parameters of a public constructor.");
            }

            if (constructors.Length == 1)
            {
                var parameters = constructors[0].GetParameters();
                return new Plan(services, constructors[0], parameters, parameters.Select(parameter => CanResolve(services, parameter)).ToArray());
            }

            Plan? best = null;
            HashSet<Type>? bestTypes = null;
            foreach (var constructor in constructors.OrderByDescending(static constructor => constructor.GetParameters().Length))
            {
                var parameters = constructor.GetParameters();
                var resolvable = parameters.Select(parameter => CanResolve(services, parameter)).ToArray();
                if (parameters.Where((parameter, index) => !resolvable[index] && !parameter.HasDefaultValue).Any())
                {
                    continue;
                }

                if (best == null)
                {
                    best = new Plan(services, constructor, parameters, resolvable);
                    continue;
                }

                // As in the container: a shorter constructor may only use parameter types the chosen one has
                bestTypes ??= best.Parameters.Select(static parameter => parameter.ParameterType).ToHashSet();
                if (parameters.Any(parameter => !bestTypes.Contains(parameter.ParameterType)))
                {
                    throw new InvalidOperationException($"Unable to activate type '{_type}'. The following constructors are ambiguous:\n{best.Constructor}\n{constructor}");
                }
            }

            return best ?? throw new InvalidOperationException($"No constructor for type '{_type}' can be instantiated using services from the service container and default values.");
        }

        private bool CanResolve(IServiceProviderIsService services, ParameterInfo parameter)
        {
            if (_serviceKey != null && parameter.IsDefined(typeof(ServiceKeyAttribute), inherit: true))
            {
                return true;
            }

            if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>(inherit: true) is { } keyed)
            {
                var key = KeyFor(keyed);
                return key == null
                    ? services.IsService(parameter.ParameterType)
                    : services is IServiceProviderIsKeyedService keyedServices && keyedServices.IsKeyedService(parameter.ParameterType, key);
            }

            return services.IsService(parameter.ParameterType);
        }

        private object? Resolve(IServiceProvider provider, ParameterInfo parameter, bool resolvable)
        {
            if (_serviceKey != null && parameter.IsDefined(typeof(ServiceKeyAttribute), inherit: true))
            {
                if (parameter.ParameterType != _serviceKey.GetType() && parameter.ParameterType != typeof(object))
                {
                    throw new InvalidOperationException($"The type of the key used for lookup doesn't match the type in the constructor parameter with the ServiceKey attribute: '{parameter.Name}' of '{_type}' is a {parameter.ParameterType}, the key a {_serviceKey.GetType()}.");
                }

                return _serviceKey;
            }

            if (!resolvable)
            {
                return DefaultValue(parameter);
            }

            if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>(inherit: true) is { } keyed && KeyFor(keyed) is { } key)
            {
                return provider.GetRequiredKeyedService(parameter.ParameterType, key);
            }

            return provider.GetRequiredService(parameter.ParameterType);
        }

        private object? KeyFor(FromKeyedServicesAttribute attribute) => InheritsKey(attribute) ? _serviceKey : attribute.Key;

        private static bool InheritsKey(FromKeyedServicesAttribute attribute)
            => LookupMode?.GetValue(attribute) is Enum mode && string.Equals(mode.ToString(), "InheritKey", StringComparison.Ordinal);

        private static object? DefaultValue(ParameterInfo parameter)
        {
            if (!parameter.HasDefaultValue)
            {
                throw new InvalidOperationException($"Unable to resolve service for type '{parameter.ParameterType}' while attempting to activate '{parameter.Member.DeclaringType}'.");
            }

            var value = parameter.DefaultValue;
            var type = parameter.ParameterType;
            if (value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            {
                // default(TStruct) is recorded as null
                return RuntimeHelpers.GetUninitializedObject(type);
            }

            return value is DBNull ? null : value;
        }

        private sealed class Plan(object container, ConstructorInfo constructor, ParameterInfo[] parameters, bool[] resolvable)
        {
            internal object Container { get; } = container;
            internal ConstructorInfo Constructor { get; } = constructor;
            internal ParameterInfo[] Parameters { get; } = parameters;
            internal bool[] Resolvable { get; } = resolvable;
        }
    }
}
