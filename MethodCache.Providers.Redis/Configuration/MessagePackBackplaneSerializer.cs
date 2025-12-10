using MessagePack;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Providers.Redis.Configuration
{
    /// <summary>
    /// Implements IBackplaneSerializer using MessagePack.
    /// </summary>
    public class MessagePackBackplaneSerializer : IBackplaneSerializer
    {
        public byte[] Serialize(BackplaneMessage message)
        {
            return MessagePackSerializer.Serialize(message);
        }

        public BackplaneMessage? Deserialize(byte[] message)
        {
            try
            {
                return MessagePackSerializer.Deserialize<BackplaneMessage>(message);
            }
            catch (MessagePackSerializationException)
            {
                // Logged at a higher level
                return null;
            }
        }
    }
}
