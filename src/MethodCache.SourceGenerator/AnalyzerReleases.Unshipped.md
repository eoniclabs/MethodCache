### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0003 | Design | Error | Reports [Cache] usage on void-returning methods.
MC0022 | Design | Error | Reports [Cache] on non-generic Task/ValueTask methods.
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
MC0034 | Design | Error | Reports a cache key argument, element or member of a type whose state is all non-public, so every value would get the same key.
MC0037 | Performance | Info | Reports a [Cache] method whose key the decorator can't write at build time, such as one with an object, interface or collection-of-collections argument, so the key generator writes and hashes it when the call runs.
MC0020 | Performance | Warning | Reports a cached result type of object, an interface or an abstract class, which a distributed cache can't restore.
MC0039 | Performance | Warning | Reports a class that derives from a type a cached result holds but that generated code can't name (generic, private, file-local, or not public in a referenced assembly), so the distributed cache can't restore it as itself.
MC0004 | Usage | Error | Reports an invalid explicit duration independently of the analyzer.
MC0021 | Usage | Error | Reports a negative explicit cache version.
MC0023 | Usage | Error | Reports a RefreshAhead that is not shorter than the Duration or SlidingExpiration on the same attribute.
MC0038 | Usage | Warning | Reports a SlidingExpiration that is not shorter than the Duration on the same attribute, so reads never extend an entry.
MC0025 | Reliability | Warning | Reports a cached result type that isn't safe to share between callers, such as JsonNode, DataTable, Stream or StringBuilder, also as an element.
