using System.Security.Cryptography;
using System.Text;
using MessagePack;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.KeyGenerators;

public class MessagePackKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (settings.Version.HasValue) keyBuilder.Append($"_v{settings.Version.Value}");

        foreach (var arg in args)
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                // Use MessagePack for deterministic serialization of complex objects
                var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                keyBuilder.Append($"_{Convert.ToBase64String(serializedArg)}");
            }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
            var base64Hash = Convert.ToBase64String(hash);
            if (settings.Version.HasValue) return $"{base64Hash}_v{settings.Version.Value}";
            return base64Hash;
        }
    }
}