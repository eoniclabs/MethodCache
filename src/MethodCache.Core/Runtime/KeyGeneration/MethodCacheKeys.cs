using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MethodCache;

/// <summary>
/// Builds the cache keys that generated decorators use, so application code can compute the same key
/// (for example to invalidate one entry) without guessing the format.
/// </summary>
/// <remarks>
/// A key has the form <c>{Service}.{Method}[:v{Version}][:{part}]*</c>, for example
/// <c>MyApp.IUserService.GetUser:v2:42</c>. Parts are formatted with the invariant culture; string parts
/// escape <c>\</c> and <c>:</c>, and <c>null</c> is written as <c>\0</c>, so no two argument lists share a key.
/// A record, class or struct argument whose public properties and fields are simple values is written as
/// <c>({member1}:{member2}...)</c> in declaration order, and an <see cref="ICacheKeyProvider"/> as its
/// <see cref="ICacheKeyProvider.CacheKeyPart"/>. An array or collection of such values (records and classes only when
/// sealed) is written as <see cref="Sequence{T}"/> writes it, after its runtime type name when that differs from the
/// declared type. Methods with other arguments, or a subclass of a declared record
/// or class, use the registered <see cref="MethodCache.Extensibility.ICacheKeyGenerator"/>, which
/// produces <c>{Service}.{Method}[:v{Version}]:{hash}</c>; <see cref="Pattern"/> matches both forms.
/// </remarks>
/// <example>
/// <code>
/// var key = MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUser), MethodCacheKeys.Part(42));
/// await cacheManager.InvalidateByKeysAsync(key);
/// </code>
/// </example>
public static class MethodCacheKeys
{
    /// <summary>The key part written for a <c>null</c> argument.</summary>
    public const string Null = "\\0";

    /// <summary>Separates the parts of a key.</summary>
    public const string Separator = ":";

    /// <summary>
    /// Returns the key prefix shared by every entry of one cached method: its service type, method name and,
    /// when set, the policy version.
    /// </summary>
    public static string Prefix(Type serviceType, string methodName, int? version = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        // Type.ToString() names generic arguments without assembly versions, so keys survive redeploys.
        var method = string.Concat(serviceType.ToString(), ".", methodName);
        return version.HasValue
            ? string.Concat(method, ":v", version.Value.ToString(CultureInfo.InvariantCulture))
            : method;
    }

    /// <summary>Returns the key for a call with the given already formatted <paramref name="parts"/>.</summary>
    public static string For(Type serviceType, string methodName, params string[] parts)
        => ForVersion(serviceType, methodName, version: null, parts);

    /// <summary>Returns the key for a call to a method whose policy has a <paramref name="version"/>.</summary>
    public static string ForVersion(Type serviceType, string methodName, int? version, params string[] parts)
    {
        var prefix = Prefix(serviceType, methodName, version);
        return parts is { Length: > 0 }
            ? string.Concat(prefix, Separator, string.Join(Separator, parts))
            : prefix;
    }

    /// <summary>Returns a wildcard pattern that matches every entry of one cached method.</summary>
    public static string Pattern(Type serviceType, string methodName, int? version = null)
        => string.Concat(Prefix(serviceType, methodName, version), "*");

    /// <summary>Formats a string argument, escaping characters that would make keys ambiguous.</summary>
    public static string Part(string? value)
    {
        if (value is null)
        {
            return Null;
        }

        return value.AsSpan().IndexOfAny('\\', ':') < 0
            ? value
            : value.Replace("\\", "\\\\").Replace(":", "\\:");
    }

    /// <summary>Formats a <see cref="bool"/> argument.</summary>
    public static string Part(bool value) => value ? "true" : "false";

    /// <summary>Formats a <see cref="char"/> argument.</summary>
    public static string Part(char value) => Part(value.ToString());

    /// <summary>Formats a <see cref="byte"/> argument.</summary>
    public static string Part(byte value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an <see cref="sbyte"/> argument.</summary>
    public static string Part(sbyte value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="short"/> argument.</summary>
    public static string Part(short value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="ushort"/> argument.</summary>
    public static string Part(ushort value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an <see cref="int"/> argument.</summary>
    public static string Part(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="uint"/> argument.</summary>
    public static string Part(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="long"/> argument.</summary>
    public static string Part(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="ulong"/> argument.</summary>
    public static string Part(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="float"/> argument so that it round-trips.</summary>
    public static string Part(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="double"/> argument so that it round-trips.</summary>
    public static string Part(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="decimal"/> argument.</summary>
    public static string Part(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="Guid"/> argument.</summary>
    public static string Part(Guid value) => value.ToString("N");

    /// <summary>
    /// Formats a <see cref="DateTime"/> argument with full precision. Local times are converted to UTC so that
    /// servers in different time zones produce the same key for the same instant.
    /// </summary>
    public static string Part(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="DateTimeOffset"/> argument with full precision and its offset.</summary>
    public static string Part(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a collection argument as <c>[{count}:{element}:{element}...]</c>, and <c>null</c> as <see cref="Null"/>.
    /// <paramref name="part"/> formats each element, and its text is escaped like a string part, so an element can't
    /// read as two; the count keeps the last element apart from the next argument. No two argument lists share a key.
    /// </summary>
    /// <example>
    /// <code>
    /// var key = MethodCacheKeys.For(typeof(IProductService), nameof(IProductService.GetMany),
    ///     MethodCacheKeys.Sequence(ids, static id => MethodCacheKeys.Part(id)));
    /// </code>
    /// </example>
    public static string Sequence<T>(IEnumerable<T>? values, Func<T, string> part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (values is null)
        {
            return Null;
        }

        var builder = new StringBuilder();
        var count = 0;
        foreach (var value in values)
        {
            builder.Append(Separator).Append(Part(part(value)));
            count++;
        }

        return string.Concat("[", count.ToString(CultureInfo.InvariantCulture), builder.ToString(), "]");
    }

    /// <summary>Formats an enum argument by its numeric value, so renaming a member keeps existing keys.</summary>
    public static string Part<TEnum>(TEnum value) where TEnum : struct, Enum => Type.GetTypeCode(typeof(TEnum)) switch
    {
        // Not ToString("D"): it writes a negative value with the current culture's minus sign (U+2212 in sv-SE)
        TypeCode.SByte => Unsafe.As<TEnum, sbyte>(ref value).ToString(CultureInfo.InvariantCulture),
        TypeCode.Int16 => Unsafe.As<TEnum, short>(ref value).ToString(CultureInfo.InvariantCulture),
        TypeCode.Int32 => Unsafe.As<TEnum, int>(ref value).ToString(CultureInfo.InvariantCulture),
        TypeCode.Int64 => Unsafe.As<TEnum, long>(ref value).ToString(CultureInfo.InvariantCulture),
        _ => value.ToString("D"), // unsigned: no sign to format
    };
}
