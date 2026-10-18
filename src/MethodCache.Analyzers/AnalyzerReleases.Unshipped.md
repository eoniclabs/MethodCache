### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0001 | Usage | Warning | Reports [Cache] or [CacheInvalidate] on a class, struct or record member, where it has no effect.
MC0004 | Usage | Error | Reports Duration, SlidingExpiration, RefreshAhead, FailSafe or FailSafeTimeout values on [Cache] or [assembly: CacheInterface] that are not positive time spans.
MC0021 | Usage | Error | Reports a negative explicit cache version.
