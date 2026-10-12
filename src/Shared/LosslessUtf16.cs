using System.Buffers;
using System.Buffers.Binary;

namespace MethodCache.Infrastructure;

// Compile privately into each package; no add-on depends on another assembly's internal methods.
internal static class LosslessUtf16
{
    internal static byte[] GetBytes(string value)
    {
        var bytes = new byte[checked(value.Length * sizeof(char))];
        Write(value, bytes);
        return bytes;
    }

    internal static void Write(string value, Span<byte> destination)
    {
        for (var i = 0; i < value.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * sizeof(char)), value[i]);
    }

    internal static string Read(ReadOnlySequence<byte> bytes)
    {
        if ((bytes.Length & 1) != 0) throw new ArgumentException("UTF-16 requires complete code units.", nameof(bytes));
        return string.Create(checked((int)(bytes.Length / sizeof(char))), bytes, static (chars, source) =>
        {
            var reader = new SequenceReader<byte>(source);
            for (var i = 0; i < chars.Length; i++)
            {
                if (!reader.TryReadLittleEndian(out short unit)) throw new FormatException("Incomplete UTF-16 code unit.");
                chars[i] = unchecked((char)unit);
            }
        });
    }
}
