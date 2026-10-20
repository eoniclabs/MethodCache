using System.Globalization;
using MethodCache.Generated;

namespace MethodCache.TestSupport;

/// <summary>
/// Writes the keys generated decorators build, for tests that pin the key format itself. Application code gets keys
/// from <see cref="IMethodCacheKeys"/>; tests of the format need the parts spelled out.
/// </summary>
internal static class TestKeys
{
    public const string Null = KeyParts.Null;

    public const string Separator = KeyParts.Separator;

    /// <summary>The prefix shared by every key of one method: <c>[Assembly]{Service}.{Method}[:v{Version}]</c>.</summary>
    public static string Prefix(Type serviceType, string methodName, int? version = null)
        => MethodCacheKeys.Prefix(serviceType, methodName, version);

    public static string For(Type serviceType, string methodName, params string[] parts)
        => ForVersion(serviceType, methodName, version: null, parts);

    public static string ForVersion(Type serviceType, string methodName, int? version, params string[] parts)
    {
        var prefix = Prefix(serviceType, methodName, version);
        return parts is { Length: > 0 } ? string.Concat(prefix, Separator, string.Join(Separator, parts)) : prefix;
    }

    /// <summary>The method name in the keys of a keyed registration, such as <c>Get@ecb</c>.</summary>
    public static string KeyedMethodName(string methodName, object serviceKey)
        => string.Concat(methodName, "@", MethodCacheKeys.ServiceKeyPart(serviceKey)
            ?? throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A {serviceKey.GetType()} service key has no key form."),
                nameof(serviceKey)));

    /// <summary>A closed type argument as a generic method name writes it.</summary>
    public static string TypePart(Type type) => KeyParts.TypeIdentity(type);

    public static string Sequence<T>(IEnumerable<T>? values, Func<T, string> part) => KeyParts.Sequence(values, part);
}
