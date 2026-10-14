using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
namespace MethodCache.Infrastructure
{
    /// <summary>
    /// How to wrap one service interface in its generated caching decorator.
    /// </summary>
    internal sealed class DecoratorRegistration
    {
        /// <param name="serviceType">The interface with cache attributes.</param>
        /// <param name="decorate">Creates the decorator around the inner service.</param>
        /// <param name="addPolicies">Registers the policies generated from the interface's attributes.</param>
        /// <param name="implementationType">
        /// The implementation found next to the interface at compile time, registered when the application has
        /// not registered the interface itself; null when there is none.
        /// </param>
        public DecoratorRegistration(
            Type serviceType,
            Func<IServiceProvider, object, object> decorate,
            Action<IServiceCollection>? addPolicies = null,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? implementationType = null)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            Decorate = decorate ?? throw new ArgumentNullException(nameof(decorate));
            GeneratedIn = decorate.Method.Module.Assembly;
            AddPolicies = addPolicies;
            ImplementationType = implementationType;
        }

        /// <summary>
        /// The interface with cache attributes.
        /// </summary>
        public Type ServiceType { get; }

        /// <summary>
        /// Creates the decorator around the inner service.
        /// </summary>
        public Func<IServiceProvider, object, object> Decorate { get; }

        /// <summary>
        /// Registers the policies generated from the interface's attributes.
        /// </summary>
        public Action<IServiceCollection>? AddPolicies { get; }

        /// <summary>
        /// The implementation found next to the interface at compile time, if any.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type? ImplementationType { get; }

        /// <summary>The assembly whose generated code created this registration.</summary>
        internal Assembly GeneratedIn { get; set; } = null!;
    }
}
