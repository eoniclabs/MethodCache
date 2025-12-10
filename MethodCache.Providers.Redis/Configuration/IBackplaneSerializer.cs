using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Providers.Redis.Configuration
{
    /// <summary>
    /// Defines the contract for serializing and deserializing backplane messages.
    /// </summary>
    public interface IBackplaneSerializer
    {
        /// <summary>
        /// Serializes a backplane message to a byte array.
        /// </summary>
        /// <param name="message">The message to serialize.</param>
        /// <returns>The serialized message as a byte array.</returns>
        byte[] Serialize(BackplaneMessage message);

        /// <summary>
        /// Deserializes a byte array to a backplane message.
        /// </summary>
        /// <param name="message">The byte array message to deserialize.</param>
        /// <returns>The deserialized backplane message.</returns>
        BackplaneMessage? Deserialize(byte[] message);
    }
}
