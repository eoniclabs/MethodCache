# Complete telemetry-probe warmup

The 388050f macOS unit job fails the existing no-listener allocation fact with 272 bytes instead of zero.
The exact source of those bytes is unproven. API warmup existed, but the first invocation of the measured
NoInlining method, including its allocation-counter calls and loop/JIT path, was still the measured window.

The fixture now runs that complete probe once as a fixed warmup, then exactly once as the measured window.
It still requires zero bytes across 10000 Request/BeginLoad pairs, and rejects any unexpected operation.
It never retries until zero or selects a minimum. A negative guard injects one escaping object only in
the measured window and proves even a single real allocation remains visible. Injecting escaping arrays
into each actual Request/BeginLoad API also fails the zero assertion; production source is restored
byte-for-byte before the green run. No runtime cache/telemetry behavior changed.

All 1039 Core facts pass locally (one opt-in soak skip), and the full fourteen-project unit job passes.
The manifest derives its total from the actual log. Latest-head external CI remains required; this
improved warmup does not prove the original 272-byte object's type or causal origin.
