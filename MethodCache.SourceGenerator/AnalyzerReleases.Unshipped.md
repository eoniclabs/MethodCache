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
MCG008 | MethodCacheGenerator | Info | Warns about sync methods using async cache infrastructure.
MCG009 | MethodCacheGenerator | Warning | Reports dynamic tags referencing unknown parameters.
