using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Options;
using MethodCache.Providers.SqlServer.Configuration;

namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Default implementation of ISqlServerSerializer supporting multiple serialization formats.
/// </summary>
public class SqlServerSerializer : ISqlServerSerializer
{
    private readonly SqlServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly MessagePackSerializerOptions _messagePackOptions;

    public SqlServerSerializer(IOptions<SqlServerOptions> options)
    {
        _options = options.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Configure MessagePack to handle objects without attributes
        _messagePackOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);
    }

    public async Task<byte[]?> SerializeAsync<T>(T value)
    {
        if (value == null)
            return null;

        return _options.DefaultSerializer switch
        {
            SqlServerSerializerType.Json => await SerializeJsonAsync(value),
            SqlServerSerializerType.MessagePack => await SerializeMessagePackAsync(value),
            SqlServerSerializerType.Binary => await SerializeBinaryAsync(value),
            _ => await SerializeMessagePackAsync(value) // Default fallback
        };
    }

    public async Task<T?> DeserializeAsync<T>(byte[]? data)
    {
        if (data == null || data.Length == 0)
            return default(T);

        return _options.DefaultSerializer switch
        {
            SqlServerSerializerType.Json => await DeserializeJsonAsync<T>(data),
            SqlServerSerializerType.MessagePack => await DeserializeMessagePackAsync<T>(data),
            SqlServerSerializerType.Binary => await DeserializeBinaryAsync<T>(data),
            _ => await DeserializeMessagePackAsync<T>(data) // Default fallback
        };
    }

    private async Task<byte[]> SerializeJsonAsync<T>(T value)
    {
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions);
        return stream.ToArray();
    }

    private async Task<T?> DeserializeJsonAsync<T>(byte[] data)
    {
        await using var stream = new MemoryStream(data);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions);
    }

    private Task<byte[]> SerializeMessagePackAsync<T>(T value)
    {
        var data = MessagePackSerializer.Serialize(value, _messagePackOptions);
        return Task.FromResult(data);
    }

    private Task<T?> DeserializeMessagePackAsync<T>(byte[] data)
    {
        var value = MessagePackSerializer.Deserialize<T>(data, _messagePackOptions);
        return Task.FromResult<T?>(value);
    }

    private Task<byte[]> SerializeBinaryAsync<T>(T value)
    {
        // Using System.Text.Json as binary serializer fallback since BinaryFormatter is obsolete
        // In a real implementation, you might want to use a more robust binary serializer
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        var data = System.Text.Encoding.UTF8.GetBytes(json);
        return Task.FromResult(data);
    }

    private Task<T?> DeserializeBinaryAsync<T>(byte[] data)
    {
        // Using System.Text.Json as binary serializer fallback since BinaryFormatter is obsolete
        var json = System.Text.Encoding.UTF8.GetString(data);
        var value = JsonSerializer.Deserialize<T>(json, _jsonOptions);
        return Task.FromResult(value);
    }
}