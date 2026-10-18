using System.Globalization;

namespace MethodCache.Infrastructure;

internal static class CanonicalTagPart
{
    internal static string Format(object? value) => value switch
    {
        null => MethodCacheKeys.Null,
        string text => MethodCacheKeys.Part(text),
        bool flag => MethodCacheKeys.Part(flag),
        char character => MethodCacheKeys.Part(character),
        byte number => MethodCacheKeys.Part(number),
        sbyte number => MethodCacheKeys.Part(number),
        short number => MethodCacheKeys.Part(number),
        ushort number => MethodCacheKeys.Part(number),
        int number => MethodCacheKeys.Part(number),
        uint number => MethodCacheKeys.Part(number),
        long number => MethodCacheKeys.Part(number),
        ulong number => MethodCacheKeys.Part(number),
        float number => MethodCacheKeys.Part(number),
        double number => MethodCacheKeys.Part(number),
        decimal number => MethodCacheKeys.Part(number),
        Guid id => MethodCacheKeys.Part(id),
        DateTime instant => MethodCacheKeys.Part(instant),
        DateTimeOffset instant => MethodCacheKeys.Part(instant),
        Enum enumeration => EnumPart(enumeration),
        ICacheKeyProvider provider => MethodCacheKeys.Part(provider.CacheKeyPart),
        _ => MethodCacheKeys.Part(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static string EnumPart(Enum value)
    {
        // D keeps the complete signed or unsigned underlying value, independent of named constants.
        return value.GetTypeCode() == TypeCode.UInt64
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }
}
