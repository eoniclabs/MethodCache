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
    private const byte FormatVersion = 1;

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
            WriteNullable(writer, entry.VariantKeys);
        }

        return stream.ToArray();
    }

    /// <summary>Returns <c>null</c> for data written by another format version.</summary>
    public static (HttpCacheEntry Entry, DateTimeOffset ExpiresAt)? Deserialize(byte[] data)
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
            Content = reader.ReadBytes(reader.ReadInt32()),
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
            VariantKeys = ReadNullableArray(reader)
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
        var count = reader.ReadInt32();
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
        var values = new string[reader.ReadInt32()];
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

    private static string? ReadNullableString(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static DateTimeOffset? ReadNullableDate(BinaryReader reader)
        => reader.ReadBoolean() ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero) : null;

    private static string[]? ReadNullableArray(BinaryReader reader) => reader.ReadBoolean() ? ReadArray(reader) : null;
}
