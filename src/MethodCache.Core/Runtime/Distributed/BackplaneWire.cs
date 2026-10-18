using System.Buffers;
using System.Buffers.Binary;
using MethodCache.Extensibility;

namespace MethodCache.Infrastructure;

// Contract 1 for every 1.x provider and entry format. See docs/developer/distributed-wire-v1.md.
internal static class BackplaneWire
{
    internal const byte Version = 1;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const int HeaderBytes = 8;
    private const string Prefix = "MC1:";

    internal static string Encode(BackplaneMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.InstanceId.Length > 1024) throw new ArgumentException("The sender exceeds the wire-frame limit.", nameof(message));
        var target = message.Type switch
        {
            BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation => message.Key,
            BackplaneMessageType.TagInvalidation => message.Tag,
            BackplaneMessageType.ClearAll => null,
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
        if (message.Type != BackplaneMessageType.ClearAll && target == null)
            throw new ArgumentException("An invalidation needs a target.", nameof(message));
        var size = checked(HeaderBytes + 5 + message.InstanceId.Length * 2 + 5 + 8 + (target == null ? 0 : 5 + target.Length * 2));
        if (size > MaximumBytes) throw new ArgumentException("The invalidation exceeds the wire-frame limit.", nameof(message));
        var bytes = new byte[size];
        bytes[0] = Version;
        bytes[1] = message.Type switch
        {
            BackplaneMessageType.KeyInvalidation => 1,
            BackplaneMessageType.TagInvalidation => 2,
            BackplaneMessageType.MethodInvalidation => 3,
            _ => 4
        };
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size - HeaderBytes);
        var position = HeaderBytes;
        Text(1, message.InstanceId);
        if (target != null) Text(2, target);
        bytes[position++] = 0x83; // Required timestamp; milliseconds since Unix epoch.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), sizeof(long));
        position += sizeof(int);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(position), message.Timestamp.ToUnixTimeMilliseconds());
        return Prefix + Convert.ToBase64String(bytes);

        void Text(byte id, string text)
        {
            bytes[position++] = (byte)(id | 0x80);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), text.Length * sizeof(char));
            position += sizeof(int);
            LosslessUtf16.Write(text, bytes.AsSpan(position));
            position += text.Length * sizeof(char);
        }
    }

    internal static bool TryDecode(string? wire, out BackplaneMessage message)
    {
        message = null!;
        // Bound encoded length before allocating a decode buffer. No replacement encoding of identifiers.
        if (wire == null || !wire.StartsWith(Prefix, StringComparison.Ordinal) ||
            wire.Length > Prefix.Length + ((MaximumBytes + 2) / 3) * 4) return false;
        var bytes = new byte[((wire.Length - Prefix.Length) / 4 + 1) * 3];
        if (!Convert.TryFromBase64Chars(wire.AsSpan(Prefix.Length), bytes, out var count) ||
            count < HeaderBytes || count > MaximumBytes) return false;
        var data = bytes.AsSpan(0, count);
        if (data[0] != Version || data[1] is < 1 or > 4 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)) & 0xff00) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)) != count - HeaderBytes) return false;
        string? sender = null;
        string? target = null;
        long? timestamp = null;
        var position = HeaderBytes;
        while (position < data.Length)
        {
            if (data.Length - position < 5) return false;
            var field = data[position++];
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position));
            position += sizeof(int);
            if (length < 0 || length > data.Length - position) return false;
            switch (field & 0x7f)
            {
                case 1:
                    if (sender != null || (length & 1) != 0 || length > 2048) return false;
                    sender = LosslessUtf16.Read(new ReadOnlySequence<byte>(bytes.AsMemory(position, length)));
                    break;
                case 2:
                    if (target != null || (length & 1) != 0) return false;
                    target = LosslessUtf16.Read(new ReadOnlySequence<byte>(bytes.AsMemory(position, length)));
                    break;
                case 3:
                    if (timestamp != null || length != sizeof(long)) return false;
                    timestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(position));
                    break;
                default:
                    if ((field & 0x80) != 0) return false;
                    break; // Future optional fields have no effect on invalidation identity.
            }
            position += length;
        }
        if (sender == null || timestamp == null || (data[1] == 4 ? target != null : target == null)) return false;
        DateTimeOffset created;
        try { created = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value); }
        catch (ArgumentOutOfRangeException) { return false; }
        var type = data[1] switch
        {
            1 => BackplaneMessageType.KeyInvalidation,
            2 => BackplaneMessageType.TagInvalidation,
            3 => BackplaneMessageType.MethodInvalidation,
            _ => BackplaneMessageType.ClearAll
        };
        message = new BackplaneMessage
        {
            Type = type,
            InstanceId = sender,
            Timestamp = created,
            Key = type is BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation ? target : null,
            Tag = type == BackplaneMessageType.TagInvalidation ? target : null
        };
        return true;
    }
}
