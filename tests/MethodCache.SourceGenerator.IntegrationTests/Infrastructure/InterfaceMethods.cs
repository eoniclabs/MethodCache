using System.Reflection;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

/// <summary>
/// Finds a method on a service's interfaces. Generated decorators implement interface methods explicitly, so
/// <c>service.GetType().GetMethod(name)</c> does not see them.
/// </summary>
public static class InterfaceMethods
{
    public static MethodInfo Find(object service, string name)
        => service.GetType().GetInterfaces()
               .Select(i => i.GetMethod(name))
               .FirstOrDefault(m => m != null)
           ?? throw new MissingMethodException(service.GetType().Name, name);
}
