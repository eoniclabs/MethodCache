### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MCG001 | MethodCacheGenerator | Error | Reports [Cache] usage on void-returning methods.
MCG002 | MethodCacheGenerator | Error | Reports [Cache] on non-generic Task/ValueTask methods.
MCG003 | MethodCacheGenerator | Error | Reports [Cache] usage with reference parameters or returns.
MCG004 | MethodCacheGenerator | Error | Reports conflicting [Cache] and [CacheInvalidate] attributes.
MCG005 | MethodCacheGenerator | Warning | Reports missing tags for [CacheInvalidate].
MCG006 | MethodCacheGenerator | Error | Reports [Cache] usage with pointer or function pointer parameters or returns, including arrays of them.
MCG007 | MethodCacheGenerator | Error | Reports [Cache] usage with ref struct parameters or returns.
MCG009 | MethodCacheGenerator | Warning | Reports dynamic tags on [Cache] or [CacheInvalidate] referencing unknown parameters.
MCG010 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] on a type it cannot cache.
MCG011 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] listing a method it cannot cache.
MCG012 | MethodCacheGenerator | Error | Reports an interface listed in more than one [assembly: CacheInterface].
MCG013 | MethodCacheGenerator | Error | Reports a local interface that cannot be decorated, such as one with static abstract members.
MCG014 | MethodCacheGenerator | Error | Reports [Cache] on a method that returns a lazy sequence (IEnumerable, IAsyncEnumerable, IQueryable).
MCG015 | MethodCacheGenerator | Warning | Reports a tag placeholder that is not a parameter name, such as "{product.Id}".
