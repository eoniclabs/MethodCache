using System.Buffers.Binary;

namespace MethodCache.Infrastructure;

// ASCII transport frame for arbitrary .NET strings. Encodings with replacement fallbacks cannot carry lone
// UTF-16 surrogates; write each code unit explicitly so Redis and SQL backplanes preserve tag/key identity.
internal static class LosslessWireText
{
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
