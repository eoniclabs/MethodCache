using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace MethodCache.Core
{
    public class FastHashKeyGenerator : ICacheKeyGenerator
    {
        public string GenerateKey(string methodName, object[] args, Configuration.CacheMethodSettings settings)
        {
            var sb = new StringBuilder();
            sb.Append(methodName);

            if (settings.Version.HasValue)
            {
                sb.Append($"_v{settings.Version.Value}");
            }

            foreach (var arg in args)
            {
                if (arg is ICacheKeyProvider keyProvider)
                {
                    sb.Append($"_{keyProvider.CacheKeyPart}");
                }
                else if (arg is string s)
                {
                    sb.Append($"_{s.GetHashCode()}");
                }
                else if (arg is int i)
                {
                    sb.Append($"_{i.GetHashCode()}");
                }
                else if (arg is long l)
                {
                    sb.Append($"_{l.GetHashCode()}");
                }
                else if (arg is bool b)
                {
                    sb.Append($"_{b.GetHashCode()}");
                }
                else if (arg is double d)
                {
                    sb.Append($"_{d.GetHashCode()}");
                }
                else if (arg is float f)
                {
                    sb.Append($"_{f.GetHashCode()}");
                }
                else
                {
                    // Fallback to MessagePack for complex types
                    var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                    sb.Append($"_{System.Convert.ToBase64String(serializedArg)}");
                }
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
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