using System.Globalization;
using System.Runtime.CompilerServices;
using MethodCache.Generated;

namespace MethodCache;

/// <summary>
/// Formats values the way cache keys and tags write them, and builds the key a manual entry is stored under, so
/// application code writes the same text as MethodCache does.
/// </summary>
/// <remarks>
/// <para>To get the key of a call to a cached method, use <see cref="IMethodCacheKeys.For{TService}"/>: it builds the
/// key exactly as the decorator does, including the policy version, overloads, generic arguments and keyed
/// registrations. Keys written by hand go wrong without notice when any of those change.</para>
/// <para>Use <see cref="Tag"/> for a tag written by hand, such as <c>MethodCacheKeys.Tag($"user:{id}")</c> for a
/// <c>"user:{id}"</c> tag template, or <c>Part</c> for one value: parts are formatted with the invariant culture,
/// string parts escape <c>\</c> and <c>:</c>, and <c>null</c> is written as <c>\0</c>.</para>
/// <para>An application <see cref="ICacheManager"/> stores an entry under <see cref="StoredKey"/>, so keys from
/// <see cref="IMethodCacheKeys"/> and <see cref="CacheEntryInfo.StoredKey"/> name the same entry.</para>
/// </remarks>
/// <example>
/// <code>
/// await cacheManager.InvalidateTagAsync("user:" + MethodCacheKeys.Part(userId));
/// </code>
/// </example>
public static class MethodCacheKeys
{
    /// <summary>
    /// Returns the key the built-in manager stores a manual entry under, and the key an application
    /// <see cref="ICacheManager"/> should store it under: <paramref name="key"/> for an unversioned entry, framed with
    /// <paramref name="version"/> otherwise. It is the <see cref="CacheEntryInfo.StoredKey"/> the administrator lists.
    /// </summary>
    /// <remarks>
    /// Store an entry under <c>StoredKey(key, options?.Version)</c> in <see cref="ICacheManager.GetOrCreateAsync{TState, T}"/>
    /// and look it up or remove it under <c>StoredKey(key, version)</c> in the other methods. A key from
    /// <see cref="IMethodCacheKeys"/> already carries its version and comes with <c>Version = null</c>, so it is stored
    /// as it is. Treat the framing as opaque.
    /// </remarks>
    /// <param name="key">The key passed to <see cref="ICacheManager"/>.</param>
    /// <param name="version">The non-negative version passed with it, or <c>null</c> for an unversioned entry.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <c>null</c>, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is negative.</exception>
    public static string StoredKey(string key, int? version = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key cannot be null or whitespace.", nameof(key));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version), "Version must be non-negative.");
        return VersionedKey(key, version);
    }

    /// <summary>Separates versioned keys from unversioned user keys, escaping the reserved leading NUL.</summary>
    internal static string VersionedKey(string key, int? version)
        => version.HasValue
            ? string.Concat("\0v", version.Value.ToString(CultureInfo.InvariantCulture), ":", key)
            : key[0] == '\0' ? "\0" + key : key;

    /// <summary>
    /// The key prefix shared by every entry of one cached method: its service type, key method name and, when set,
    /// the policy version.
    /// </summary>
    internal static string Prefix(Type serviceType, string methodName, int? version = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        // Assembly simple names separate identically named service types; versions never participate.
        var method = string.Concat(Infrastructure.StableTypeIdentity.For(serviceType), ".", methodName);
        return version.HasValue
            ? string.Concat(method, ":v", version.Value.ToString(CultureInfo.InvariantCulture))
            : method;
    }

    /// <summary>
    /// The service key as it appears in cache keys, or null when its type has no stable string form. Every other
    /// type's form has a '.' or '(' in it, so a string with one, or with a quote, is written in quotes: no two keys
    /// of any types share a form.
    /// </summary>
    internal static string? ServiceKeyPart(object serviceKey) => serviceKey switch
    {
        string text => text.AsSpan().IndexOfAny('.', '(', '"') < 0
            ? Part(text)
            : string.Concat("\"", Part(text).Replace("\"", "\\\""), "\""),
        Enum value => string.Concat(Infrastructure.StableTypeIdentity.For(value.GetType()), ".",
            Enum.GetName(value.GetType(), value) ?? Infrastructure.CanonicalTagPart.Format(value)),
        bool value => string.Concat("Boolean(", Part(value), ")"),
        char value => string.Concat("Char(", Part(value), ")"),
        Guid value => string.Concat("Guid(", Part(value), ")"),
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            string.Concat(serviceKey.GetType().Name, "(", ((IFormattable)serviceKey).ToString(null, CultureInfo.InvariantCulture), ")"),
        _ => null
    };

    /// <summary>
    /// Builds a tag from an interpolated string, writing each value the way the tag templates of <c>[Cache]</c> write
    /// it, so <c>MethodCacheKeys.Tag($"price:{provider}:{model}")</c> names the entries a <c>"price:{provider}:{model}"</c>
    /// template tagged: strings escape <c>:</c> and <c>\</c>, a <see cref="Guid"/> is 32 digits, an enum is its number, and
    /// numbers and dates use the invariant culture.
    /// </summary>
    /// <remarks>
    /// A value of a type MethodCache doesn't write in tags, or with a format or alignment such as <c>{price:F2}</c>,
    /// doesn't compile. To build the tag from a call to the cached method instead, use <see cref="IMethodCacheKeys.Tag{TService}"/>.
    /// </remarks>
    /// <param name="tag">The interpolated string.</param>
    /// <returns>The tag.</returns>
    /// <example>
    /// <code>
    /// await cache.InvalidateTagAsync(MethodCacheKeys.Tag($"price:{provider}:{model}"));
    /// </code>
    /// </example>
    public static string Tag(ref TagInterpolatedStringHandler tag) => tag.ToStringAndClear();

    /// <summary>Formats a string argument, escaping characters that would make keys ambiguous.</summary>
    public static string Part(string? value)
    {
        if (value is null)
        {
            return KeyParts.Null;
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

    /// <summary>Formats a <see cref="Guid"/> argument as 32 digits without hyphens (format <c>N</c>).</summary>
    public static string Part(Guid value) => value.ToString("N");

    /// <summary>
    /// Formats a <see cref="DateTime"/> argument with full precision. Local times are converted to UTC so that
    /// servers in different time zones produce the same key for the same instant.
    /// </summary>
    public static string Part(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats a <see cref="DateTimeOffset"/> argument with full precision and its offset.</summary>
    public static string Part(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

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

    // A nullable value writes as the value, or as null does in [Cache] keys and tags

    /// <summary>Formats a nullable <see cref="bool"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(bool? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="char"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(char? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="byte"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(byte? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="sbyte"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(sbyte? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="short"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(short? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="ushort"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(ushort? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="int"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(int? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="uint"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(uint? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="long"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(long? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="ulong"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(ulong? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="float"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(float? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="double"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(double? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="decimal"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(decimal? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="Guid"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(Guid? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="DateTime"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(DateTime? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable <see cref="DateTimeOffset"/> argument: the value as its own overload writes it, or the null part.</summary>
    public static string Part(DateTimeOffset? value) => value is { } item ? Part(item) : Generated.KeyParts.Null;

    /// <summary>Formats a nullable enum argument: the member's number, or the null part.</summary>
    public static string Part<TEnum>(TEnum? value) where TEnum : struct, Enum => value is { } item ? Part(item) : Generated.KeyParts.Null;
}
