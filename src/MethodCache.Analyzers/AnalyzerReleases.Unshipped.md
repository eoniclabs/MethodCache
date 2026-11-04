### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0001 | Usage | Warning | Reports [Cache] or [CacheInvalidate] on a class, struct or record member, where it has no effect.
MC0004 | Usage | Error | Reports Duration, SlidingExpiration, RefreshAhead, FailSafe, FailSafeTimeout or FailSafeRetryAfter values on [Cache] or [assembly: CacheInterface] that are not positive time spans. FailSafe, SlidingExpiration and RefreshAhead also accept "off".
MC0021 | Usage | Error | Reports a negative explicit cache version.
MC0023 | Usage | Error | Reports a RefreshAhead that is not shorter than the Duration or SlidingExpiration on [Cache] or [assembly: CacheInterface].
MC0024 | Usage | Warning | Reports a value in an interpolated tag passed to InvalidateTagAsync or InvalidateTagsAsync that string interpolation writes differently from MethodCache's tags (strings with ':' or '\', Guid, enum, bool, dates, floating-point numbers).
MC0026 | Security | Warning | Reports the implementation of a cached interface taking per-request state (IHttpContextAccessor, HttpContext, HttpRequest, ClaimsPrincipal, IPrincipal) in its constructor.
