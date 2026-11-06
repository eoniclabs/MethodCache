using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Generated;

/// <summary>
/// Finds a registered interface with <c>[Cache]</c> methods that no generated code describes: MethodCache's source
/// generator didn't run in its project, so its calls would silently run uncached.
/// </summary>
internal static class MissingGeneratedCode
{
    private static readonly string CoreAssembly = typeof(CacheAttribute).Assembly.GetName().Name!;

    /// <exception cref="InvalidOperationException">A registered interface has <c>[Cache]</c> methods but no generated code.</exception>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: when trimming removes an interface's methods or attributes it finds nothing and stays silent.")]
    internal static void ThrowIfAny(IServiceCollection services, IEnumerable<GeneratedModuleBuilder> modules)
    {
        // Native AOT always compiles the generated code it has; attributes aren't read there
        if (!RuntimeFeature.IsDynamicCodeSupported) return;

        HashSet<Type>? described = null;
        var usesMethodCache = new Dictionary<Assembly, bool>();
        foreach (var descriptor in services)
        {
            var type = descriptor.ServiceType;
            if (!type.IsInterface) continue;
            if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
            if (!usesMethodCache.TryGetValue(type.Assembly, out var uses))
                usesMethodCache[type.Assembly] = uses = type.Assembly == typeof(CacheAttribute).Assembly
                    ? false
                    : type.Assembly.GetReferencedAssemblies().Any(static a => a.Name == CoreAssembly);
            if (!uses || !HasCachedMethod(type)) continue;

            described ??= modules.SelectMany(static m => m.DeclaredServices).ToHashSet();
            if (described.Contains(type)) continue;

            var project = type.Assembly.GetName().Name;
            throw new InvalidOperationException(
                $"'{type}' has [Cache] methods, but MethodCache's source generator didn't run in {project}, so its calls " +
                $"would run uncached. Check that the project {project} gets the MethodCache package, directly or through a " +
                "project it references, and that MethodCache.SourceGenerator is among its analyzers (Dependencies > " +
                "Analyzers in Rider or Visual Studio). A build that can't load the generator reports CS9057.");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: when trimming removes an interface's methods or attributes it finds nothing and stays silent.")]
    private static bool HasCachedMethod(Type @interface)
        => @interface.GetMethods().Concat(@interface.GetInterfaces().SelectMany(static i => i.GetMethods()))
            .Any(static method => method.IsDefined(typeof(CacheAttribute), inherit: false));
}
