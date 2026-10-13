### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MCG001 | MethodCacheGenerator | Error | Reports [Cache] usage on void-returning methods.
MCG002 | MethodCacheGenerator | Error | Reports [Cache] on non-generic Task/ValueTask methods.
MCG003 | MethodCacheGenerator | Error | Reports [Cache] usage with reference parameters or returns.
MCG004 | MethodCacheGenerator | Error | Reports conflicting [Cache] and [CacheInvalidate] attributes.
MCG005 | MethodCacheGenerator | Warning | Reports missing tags for [CacheInvalidate].
MCG006 | MethodCacheGenerator | Error | Reports [Cache] usage with pointer or function pointer parameters or returns, including arrays of them.
MCG007 | MethodCacheGenerator | Error | Reports [Cache] usage with ref struct or potentially ref-like generic parameters or returns.
MCG009 | MethodCacheGenerator | Warning | Reports dynamic tags on [Cache] or [CacheInvalidate] referencing unknown parameters.
MCG010 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] on a type it cannot cache, such as one with static abstract or non-public abstract members.
MCG011 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] listing a method it cannot cache: missing, overloaded, static, or one [Cache] would reject.
MCG012 | MethodCacheGenerator | Error | Reports an interface listed in more than one [assembly: CacheInterface].
MCG013 | MethodCacheGenerator | Error | Reports an interface with cached methods that cannot be decorated because it has static abstract or non-public abstract members.
MCG014 | MethodCacheGenerator | Error | Reports [Cache] on a method that returns a lazy sequence (IEnumerable, IAsyncEnumerable, IQueryable).
MCG015 | MethodCacheGenerator | Warning | Reports a tag placeholder that is not a parameter name, such as "{product.Id}".
MCG016 | MethodCacheGenerator | Warning | Reports a tag placeholder matching multiple parameters without an exact spelling match.
MCG017 | MethodCacheGenerator | Warning | Reports [Cache] or [CacheInvalidate] on a static, non-public or sealed interface member, which the decorator can't intercept.
MCG018 | MethodCacheGenerator | Error | Reports a cache key argument, element or member whose framework type has no key form, such as StringBuilder.
MCG019 | MethodCacheGenerator | Warning | Reports a cached result type of object, an interface or an abstract class, which a distributed cache can't restore.
