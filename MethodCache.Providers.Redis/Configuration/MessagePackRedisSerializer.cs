using MessagePack;
using MessagePack.Resolvers;
using System.IO;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class MessagePackRedisSerializer : IRedisSerializer
    {
        // Use StandardResolver for better performance when types are known
        // ContractlessStandardResolver is slower but more flexible
        private static readonly MessagePackSerializerOptions _performantOptions = 
            MessagePackSerializerOptions.Standard
                .WithResolver(StandardResolver.Instance)
                .WithCompression(MessagePackCompression.Lz4BlockArray);
        
        private static readonly MessagePackSerializerOptions _contractlessOptions = 
            MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.Lz4BlockArray);

        private readonly bool _useContractless;

        public MessagePackRedisSerializer(bool useContractless = true)
        {
            _useContractless = useContractless;
        }

        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            var options = _useContractless ? _contractlessOptions : _performantOptions;
            return MessagePackSerializer.Serialize(value, options);
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            var options = _useContractless ? _contractlessOptions : _performantOptions;
            return MessagePackSerializer.Deserialize<T>(data, options);
        }

        public async Task<byte[]> SerializeAsync<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            using var stream = new MemoryStream();
            var options = _useContractless ? _contractlessOptions : _performantOptions;
            await MessagePackSerializer.SerializeAsync(stream, value, options).ConfigureAwait(false);
            return stream.ToArray();
        }

        public async Task<T> DeserializeAsync<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            using var stream = new MemoryStream(data, writable: false);
            var options = _useContractless ? _contractlessOptions : _performantOptions;
            
            return await MessagePackSerializer.DeserializeAsync<T>(stream, options);
        }
    }
}
