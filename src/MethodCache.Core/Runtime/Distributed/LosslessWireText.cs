using System.Buffers;
using MethodCache.Extensibility;

namespace MethodCache.Infrastructure;

// ASCII transport frame for arbitrary .NET strings. Encodings with replacement fallbacks cannot carry lone
// UTF-16 surrogates; write each code unit explicitly so Redis and SQL backplanes preserve tag/key identity.
internal static class LosslessWireText
{
    internal static int ToMessageType(BackplaneMessageType type) => type switch
    {
        BackplaneMessageType.KeyInvalidation => 4,
        BackplaneMessageType.TagInvalidation => 5,
        BackplaneMessageType.MethodInvalidation => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static bool TryMessageType(int type, out BackplaneMessageType logical)
    {
        logical = type == 6 ? BackplaneMessageType.MethodInvalidation : type == 5 ? BackplaneMessageType.TagInvalidation : BackplaneMessageType.KeyInvalidation;
        return type is 4 or 5 or 6;
    }

    internal static string Encode(string value)
    {
        var bytes = LosslessUtf16.GetBytes(value);
        return "4:" + Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string wire, out string value)
    {
        value = string.Empty;
        if (!wire.StartsWith("4:", StringComparison.Ordinal)) return false;
        if (wire.Length == 2) return true;
        var bytes = new byte[((wire.Length - 2) / 4 + 1) * 3];
        if (!Convert.TryFromBase64Chars(wire.AsSpan(2), bytes, out var written) || (written & 1) != 0) return false;
        value = LosslessUtf16.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, written)));
        return true;
    }
}
