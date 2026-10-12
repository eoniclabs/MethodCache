# MethodCache.Serialization.MessagePack

All built-in key generators preserve exact UTF-16 string identity, including lone surrogates in `ICacheKeyProvider.CacheKeyPart` and nested arguments. MessagePack key strings use a dedicated lossless representation.

Register `AddMethodCacheMessagePack()` to store distributed cache values with MessagePack. Use `MessagePackKeyGenerator` when argument types require MessagePack serialization. See [distributed caching](../../docs/features/distributed-cache.md) and [cache keys](../../docs/features/keys.md).
