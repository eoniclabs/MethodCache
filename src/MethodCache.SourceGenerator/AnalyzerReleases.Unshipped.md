### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MCG001 | MethodCacheGenerator | Warning | Reports [Cache] usage on void-returning methods.
MCG002 | MethodCacheGenerator | Warning | Reports [Cache] on non-generic Task/ValueTask methods.
MCG003 | MethodCacheGenerator | Warning | Reports [Cache] usage with ref/out/in parameters.
MCG004 | MethodCacheGenerator | Error | Reports conflicting [Cache] and [CacheInvalidate] attributes.
MCG005 | MethodCacheGenerator | Warning | Reports missing tags for [CacheInvalidate].
MCG006 | MethodCacheGenerator | Error | Reports [Cache] usage with pointer types.
MCG007 | MethodCacheGenerator | Error | Reports [Cache] usage with ref struct parameters.
MCG009 | MethodCacheGenerator | Warning | Reports dynamic tags referencing unknown parameters.
MCG010 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] on a type it cannot cache.
MCG011 | MethodCacheGenerator | Error | Reports [assembly: CacheInterface] listing a method it cannot cache.
MCG012 | MethodCacheGenerator | Error | Reports an interface listed in more than one [assembly: CacheInterface].
MCG013 | MethodCacheGenerator | Error | Reports a local interface that cannot be decorated, such as one with static abstract members.
