### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|------
MC0001 | Usage | Warning | Warns when [Cache] is applied to non-virtual class methods.
MC0002 | Usage | Warning | Warns when [CacheInvalidate] is used on non-async methods.
MC0003 | Usage | Info | Informs when configuration overrides attribute-based cache settings.
MC0004 | Usage | Error | Reports Duration, SlidingExpiration or RefreshAhead values on [Cache] that are not positive time spans.
