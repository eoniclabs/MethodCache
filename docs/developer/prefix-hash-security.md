# Prefix hashing review

The current candidate uses the **Marvin32 randomized string hash used by .NET 10** with an independent
64-bit process-secret seed from `RandomNumberGenerator`. Exact UTF-16 little-endian input is preserved,
including isolated surrogates. Immutable invocation snapshots retain unfinished prefix state; a lookup
finishes a copy with its current suffix. Every dictionary path uses this algorithm and seed. Equality
always compares the complete key ordinally. Hashes and seeds never become external identities.

The security basis is the runtime's [randomized string comparer](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/RandomizedStringEqualityComparer.cs)
and [Marvin implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Marvin.cs).
The runtime's [nonrandomized comparer](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/NonRandomizedStringEqualityComparer.cs)
switches to that randomized algorithm when collisions exceed its threshold. This custom comparer starts
with that defense immediately; it cannot rely on the dictionary recognizing it and performing that switch.
This is a specific runtime-vetted keyed algorithm, not an arbitrary fast hash with a random initial value.

This does **not** make Marvin a cryptographic PRF or establish cryptographic collision resistance.
The secret has 64 bits and the output 32 bits. Ordinary birthday collisions, an adaptive online oracle,
resource exhaustion through request volume or very long inputs, and compromised application code remain
outside the guarantee. The intended protection is against cheaply preparing a reusable offline collision
family without knowing the secret, matching the randomized-comparer defense used by the runtime.
Application admission, authorization and request limits remain necessary. The change does not claim
stronger protection than .NET's randomized strings. No seed/state/hash is exposed through logs, metrics,
admin, L2 or wire frames.

The byte-stream implementation is checked against a separately vendored, MIT-licensed .NET v10.0.0
reference algorithm across all byte splits and several fixed seeds. CsCheck covers arbitrary seeds,
UTF-16 splits, empty parts, growth and append after lookup, including the specialized UTF-16 finalizer.
Deterministic Aa/BB polynomial-collision families, long common prefixes, NUL and isolated surrogates
are assessed using bounded equality-chain work, not fragile nanosecond assertions. These cases are
regression evidence; their distribution alone is not the security argument. Forced-collision engine
tests cover every key path, concurrent publication, removal and frequency/eviction behavior.

A per-thread memo retains at most one immutable string of at most 2048 UTF-16 code units and its hash.
It checks **reference identity**, so another owner is independently hashed; larger keys are not retained.
It only avoids hashing the same owned key again during lookup/publication/eviction. The process seed
never changes, and test-only collision masks are applied after this memo. It is not a value cache.

The earlier SipHash-2-4 candidate had correct author vectors and unchanged allocations but failed the
section-10 timing gate (see design-prefix-preflight.md). That failed implementation remains in history;
it is not silently counted as an approved optimization. The current algorithm and full-path results
must pass the same functional, frozen-vendor/AOT, backend-identity and timing gates before completion.
