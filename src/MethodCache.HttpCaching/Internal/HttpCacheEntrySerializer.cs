using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Compact binary form of an <see cref="HttpCacheEntry"/> for the distributed cache. It needs no reflection, so it
/// works under Native AOT and whatever serializer the rest of the app uses.
/// </summary>
internal static class HttpCacheEntrySerializer
{
    internal const byte FormatVersion = 2;

    public static byte[] Serialize(HttpCacheEntry entry, DateTimeOffset expiresAt)
    {
        using var stream = new MemoryStream(entry.Content.Length + 512);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(expiresAt.UtcTicks);
            writer.Write(entry.RequestUri);
            writer.Write(entry.Method);
            writer.Write((int)entry.StatusCode);
            writer.Write(entry.Content.Length);
            writer.Write(entry.Content);
            WriteHeaders(writer, entry.Headers);
            WriteHeaders(writer, entry.ContentHeaders);
            WriteNullable(writer, entry.ETag);
            WriteNullable(writer, entry.LastModified);
            WriteNullable(writer, entry.CacheControl?.ToString());
            WriteNullable(writer, entry.Expires);
            WriteNullable(writer, entry.Date);
            writer.Write(entry.StoredAt.UtcTicks);
            writer.Write(entry.InitialAge.Ticks);
            WriteNullable(writer, entry.VaryHeaders);
            WriteNullable(writer, entry.VariantGeneration);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Returns <c>null</c> for data written by another format version, and for data that is not a valid entry: the
    /// distributed cache is outside this process, so a length is never trusted beyond the bytes that are there.
    /// </summary>
    public static (HttpCacheEntry Entry, DateTimeOffset ExpiresAt)? Deserialize(byte[] data)
    {
        try
        {
            return Read(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (HttpCacheEntry Entry, DateTimeOffset ExpiresAt)? Read(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        if (data.Length == 0 || reader.ReadByte() != FormatVersion)
        {
            return null;
        }

        var expiresAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var entry = new HttpCacheEntry
        {
            RequestUri = reader.ReadString(),
            Method = reader.ReadString(),
            StatusCode = (HttpStatusCode)reader.ReadInt32(),
            Content = reader.ReadBytes(ReadCount(reader, bytesPerItem: 1)),
            Headers = ReadHeaders(reader),
            ContentHeaders = ReadHeaders(reader),
            ETag = ReadNullableString(reader),
            LastModified = ReadNullableDate(reader),
            CacheControl = ReadNullableString(reader) is { } cacheControl && CacheControlHeaderValue.TryParse(cacheControl, out var parsed) ? parsed : null,
            Expires = ReadNullableDate(reader),
            Date = ReadNullableDate(reader),
            StoredAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
            InitialAge = TimeSpan.FromTicks(reader.ReadInt64()),
            VaryHeaders = ReadNullableArray(reader),
            VariantGeneration = ReadNullableString(reader)
        };

        return (entry, expiresAt);
    }

    private static void WriteHeaders(BinaryWriter writer, Dictionary<string, string[]> headers)
    {
        writer.Write(headers.Count);
        foreach (var (name, values) in headers)
        {
            writer.Write(name);
            WriteArray(writer, values);
        }
    }

    private static Dictionary<string, string[]> ReadHeaders(BinaryReader reader)
    {
        var count = ReadCount(reader, bytesPerItem: 2); // a name and a value count, at least one byte each
        var headers = new Dictionary<string, string[]>(count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            headers[reader.ReadString()] = ReadArray(reader);
        }

        return headers;
    }

    private static void WriteArray(BinaryWriter writer, string[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static string[] ReadArray(BinaryReader reader)
    {
        var values = new string[ReadCount(reader, bytesPerItem: 1)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadString();
        }

        return values;
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null)
        {
            writer.Write(value);
        }
    }

    private static void WriteNullable(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value.UtcTicks);
        }
    }

    private static void WriteNullable(BinaryWriter writer, string[]? values)
    {
        writer.Write(values != null);
        if (values != null)
        {
            WriteArray(writer, values);
        }
    }

    /// <summary>
    /// Reads a count of items that take at least <paramref name="bytesPerItem"/> bytes each, and refuses one the rest
    /// of the data cannot hold, so a crafted entry can't make the reader allocate gigabytes.
    /// </summary>
    private static int ReadCount(BinaryReader reader, int bytesPerItem)
    {
        var count = reader.ReadInt32();
        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (count < 0 || (long)count * bytesPerItem > remaining)
        {
            throw new InvalidDataException("The cached HTTP entry is not valid.");
        }

        return count;
    }

    private static string? ReadNullableString(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static DateTimeOffset? ReadNullableDate(BinaryReader reader)
        => reader.ReadBoolean() ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero) : null;

    private static string[]? ReadNullableArray(BinaryReader reader) => reader.ReadBoolean() ? ReadArray(reader) : null;
}
