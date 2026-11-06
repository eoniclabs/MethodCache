### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0001 | Usage | Warning | Reports [Cache] or [CacheInvalidate] on a class, struct or record member, where it has no effect.
MC0004 | Usage | Error | Reports Duration, SlidingExpiration, RefreshAhead, FailSafe, FailSafeTimeout or FailSafeRetryAfter values on [Cache] or [assembly: CacheInterface] that are not positive time spans. FailSafe, SlidingExpiration and RefreshAhead also accept "off".
MC0021 | Usage | Error | Reports a negative explicit cache version.
MC0023 | Usage | Error | Reports a RefreshAhead that is not shorter than the Duration or SlidingExpiration on [Cache] or [assembly: CacheInterface].
MC0024 | Usage | Warning | Reports a value in an interpolated tag passed to InvalidateTagAsync, InvalidateTagsAsync or InvalidateAfterCommit that string interpolation writes differently from MethodCache's tags (strings with ':' or '\', Guid, enum, bool, dates, floating-point numbers).
MC0026 | Security | Warning | Reports the implementation of a cached interface taking per-request state (IHttpContextAccessor, HttpContext, HttpRequest, ClaimsPrincipal, IPrincipal) in its constructor, or a class whose constructor takes it, directly or as a type argument such as IDbContextFactory<T>.
MC0027 | Reliability | Warning | Reports ExecuteUpdate and ExecuteDelete on entity types mapped in the project or a project it references (all types when none is mapped) and ExecuteSql* SQL that names a mapped type's table, runs a procedure or block, or can't be read (any SQL other than selects and set statements when none is mapped), in a project that uses MethodCache.EntityFrameworkCore, when the method around them doesn't invalidate anything.
MC0028 | Reliability | Warning | Reports a tag on a cached method that no invalidation in the project names ([CacheInvalidate], also on an interface of a referenced project, EF Core mapping, InvalidateTagAsync, InvalidateTagsAsync, InvalidateAfterCommit or an [InvalidatesCache] call) and no [Cache] marks InvalidatedElsewhere, in a project that invalidates something.
MC0029 | Usage | Warning | Reports a tag in InvalidatedElsewhere on [Cache] that matches none of the method's Tags.
MC0030 | Usage | Warning | Reports a program (not a test project) with cached services, its own or a referenced project's, when neither it nor a referenced project calls AddMethodCache.
MC0031 | Usage | Warning | Reports a call a class makes to its own cached method, which skips the cache.
