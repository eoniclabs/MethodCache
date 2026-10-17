# Public telemetry-hit probe initialization

The actual f7520375 Linux CI test fails with 4,480 allocated bytes in its first
probe window; the same run's coverage pass and later independent original-code
runs pass. The public cache was warmed, but the probe's counter/loop path itself
had never executed. This change follows the existing Core no-listener probe:
warm the complete probe once, then measure **one fixed window requiring exactly
zero bytes**, without retries, minima, new tolerances or allocation suppression.

A new negative control injects one escaping object inside the measured window and
must detect it. Each window also asserts exactly 1,000 actual Core callbacks and
one origin load overall, so a disabled listener or uncached path cannot silently
pass. Every assertion and setup step stays outside the allocation counter window.

The pinned Linux check retains ten fresh original-code processes (all pass,
confirming that the original CI issue is intermittent), ten fresh candidate
processes (both facts pass in each), and the complete 120-test Docs suite (zero
failures/skips). Every TRX is independently checked for executed/pass counts.
The Core DLL hash is identical before/after; only test source changes. Local Mac
targeted facts, all120Docs and formatting also pass. Final published-head CI is
still required. This does not establish an exact runtime cause for the transient
original bytes or change any library allocation promise.

All 52 original archived file hashes verify. Source, frozen patch, controller,
all twenty fresh-process logs/TRX files and the original failing CI log are
retained. Source bundle/binaries remain private build inputs; only their hashes
are retained here. Archive SHA-256:
`f30cea00297c32132bf50981ae4aecd1b67772fec2e92c7ab3a06a5335c7fe64`.
