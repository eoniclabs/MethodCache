using System.Text.Json;
using MethodCache.Core.Storage.Abstractions;
using System.Text;

namespace MethodCache.Providers.Redis.Configuration
{
    /// <summary>
    /// Implements IBackplaneSerializer using System.Text.Json.
    /// </summary>
    public class JsonBackplaneSerializer : IBackplaneSerializer
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new();

        public byte[] Serialize(BackplaneMessage message)
        {
            var jsonString = JsonSerializer.Serialize(message, s_jsonOptions);
            return Encoding.UTF8.GetBytes(jsonString);
        }

        public BackplaneMessage? Deserialize(byte[] message)
        {
            try
            {
                var jsonString = Encoding.UTF8.GetString(message);
                return JsonSerializer.Deserialize<BackplaneMessage>(jsonString, s_jsonOptions);
            }
            catch (JsonException)
            {
                // Logged at a higher level
                return null;
            }
        }
    }
}
