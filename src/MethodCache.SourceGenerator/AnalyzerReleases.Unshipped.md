### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0003 | Design | Error | Reports [Cache] usage on void-returning methods.
MC0004 | Design | Error | Reports [Cache] on non-generic Task/ValueTask methods.
MC0005 | Design | Error | Reports [Cache] usage with reference parameters or returns.
MC0006 | Usage | Error | Reports conflicting [Cache] and [CacheInvalidate] attributes.
MC0007 | Usage | Warning | Reports missing tags for [CacheInvalidate].
MC0008 | Design | Error | Reports [Cache] usage with pointer or function pointer parameters or returns, including arrays of them.
MC0009 | Design | Error | Reports [Cache] usage with ref struct or potentially ref-like generic parameters or returns.
MC0010 | Usage | Warning | Reports dynamic tags on [Cache] or [CacheInvalidate] referencing unknown parameters.
MC0011 | Design | Error | Reports [assembly: CacheInterface] on a type it cannot cache, such as one with static abstract or non-public abstract members.
MC0012 | Design | Error | Reports [assembly: CacheInterface] listing a method it cannot cache: missing, overloaded, static, or one [Cache] would reject.
MC0013 | Design | Error | Reports an interface listed in more than one [assembly: CacheInterface].
MC0014 | Design | Error | Reports an interface with cached methods that cannot be decorated because it has static abstract or non-public abstract members.
MC0015 | Design | Error | Reports [Cache] on a method that returns a lazy sequence (IEnumerable, IAsyncEnumerable, IQueryable).
MC0016 | Usage | Warning | Reports a tag placeholder that is not a parameter name, such as "{product.Id}".
MC0017 | Usage | Warning | Reports a tag placeholder matching multiple parameters without an exact spelling match.
MC0018 | Usage | Warning | Reports [Cache] or [CacheInvalidate] on a static, non-public or sealed interface member, which the decorator can't intercept.
MC0019 | Design | Error | Reports a cache key argument, element or member whose framework type has no key form, such as StringBuilder.
MC0020 | Performance | Warning | Reports a cached result type of object, an interface or an abstract class, which a distributed cache can't restore.
MC0002 | Usage | Error | Reports an invalid explicit duration independently of the analyzer.
MC0021 | Usage | Error | Reports a negative explicit cache version.
