using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MethodCache.Core
{
    public class JsonKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, Configuration.CacheMethodSettings settings)
        {
            var keyBuilder = new StringBuilder();
            keyBuilder.Append(methodName);

            if (settings.Version.HasValue)
            {
                keyBuilder.Append($"_v{settings.Version.Value}");
            }

            foreach (var arg in args)
            {
                if (arg is ICacheKeyProvider keyProvider)
                {
                    keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
                }
                else
                {
                    // Use System.Text.Json for human-readable serialization
                    var serializedArg = JsonSerializer.Serialize(arg, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve });
                    keyBuilder.Append($"_{serializedArg}");
                }
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
                var base64Hash = System.Convert.ToBase64String(hash);
                if (settings.Version.HasValue)
                {
                    return $"{base64Hash}_v{settings.Version.Value}";
                }
                return base64Hash;
            }
        }
    }
}
