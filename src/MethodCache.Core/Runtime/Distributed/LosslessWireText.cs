using System.Buffers.Binary;
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
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static bool TryMessageType(int type, out BackplaneMessageType logical)
    {
        logical = type == 5 ? BackplaneMessageType.TagInvalidation : BackplaneMessageType.KeyInvalidation;
        return type is 4 or 5;
    }

    internal static string Encode(string value)
    {
        var bytes = new byte[checked(value.Length * 2)];
        for (var i = 0; i < value.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), value[i]);
        return "4:" + Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string wire, out string value)
    {
        value = string.Empty;
        if (!wire.StartsWith("4:", StringComparison.Ordinal)) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(wire[2..]); }
        catch (FormatException) { return false; }
        if ((bytes.Length & 1) != 0) return false;
        var chars = new char[bytes.Length / 2];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        value = new string(chars);
        return true;
    }
}
