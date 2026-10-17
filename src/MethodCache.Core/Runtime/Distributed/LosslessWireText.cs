using System.Buffers;

namespace MethodCache.Infrastructure;

// Stable method-identity encoding for metadata keys; independent of the backplane protocol.
// Write UTF-16 code units explicitly to preserve lone surrogates without replacement fallbacks.
internal static class LosslessWireText
{
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
