namespace MethodCache.Providers.SqlServer.Services;

/// <summary>
/// Handles serialization and deserialization of cache values for SQL Server storage.
/// </summary>
public interface ISqlServerSerializer
{
    /// <summary>
    /// Serializes a value to byte array for storage.
    /// </summary>
    /// <typeparam name="T">Type of value to serialize.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <returns>Serialized byte array, or null if value is null.</returns>
    Task<byte[]?> SerializeAsync<T>(T value);

    /// <summary>
    /// Deserializes a byte array back to the original type.
    /// </summary>
    /// <typeparam name="T">Type to deserialize to.</typeparam>
    /// <param name="data">Serialized byte array.</param>
    /// <returns>Deserialized value.</returns>
    Task<T?> DeserializeAsync<T>(byte[]? data);
}