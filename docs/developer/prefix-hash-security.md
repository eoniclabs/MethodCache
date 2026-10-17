# Prefix hashing review

Generated-method dictionaries hash keys with **SipHash-1-3** under independent 128-bit process-secret keys from
`RandomNumberGenerator`, one per input encoding. SipHash is a keyed hash designed against hash flooding ([Aumasson
and Bernstein](https://www.aumasson.jp/siphash/siphash.pdf)); SipHash-1-3, one compression round and three
finalization rounds, is the variant Python 3.11+ and Rust use for their hash tables. A key whose code units are all
ASCII (below 0x80) is hashed as one byte per code unit, which halves the compressions for typical keys; any other
key as its exact UTF-16 code units in little-endian byte order, including isolated surrogates. Each encoding has
its own key, so an ASCII key and a UTF-16 key with the same bytes have unrelated hashes. Immutable invocation
snapshots retain the unfinished prefix state in both encodings; a lookup finishes a copy in locals with its current
suffix, in the ASCII form only when the prefix and the suffix are both ASCII. Every dictionary path makes the same
choice for the same key text and uses this algorithm and these keys. Equality always compares the complete key
ordinally. Hashes and keys never become external identities.

This replaces the Marvin32 candidate (the .NET randomized string hash). Marvin has a 64-bit seed and is not a
cryptographic PRF; SipHash has a 128-bit key and is built to be one. SipHash-2-4 is the authors' conservative
choice; SipHash-1-3 trades rounds for speed, as Python and Rust do for the same use.

The guarantee is still limited. The dictionary uses 32 bits of the output, so ordinary birthday collisions remain.
An adaptive online oracle, resource exhaustion through request volume or very long inputs, and compromised
application code are outside it. The intended protection is against preparing a collision family without knowing
the key. Application admission, authorization and request limits remain necessary. No key/state/hash is exposed
through logs, metrics, admin, L2 or wire frames.

The implementation is checked against the 64 published SipHash-1-3 vectors from Rust's core library tests: every
even-length vector, as UTF-16, at every pair of split points between prefix, appended text and finished suffix. All
vector inputs are below 0x80, so every vector also checks the one-byte form at every pair of split points. A
separately written byte-oriented reference in the tests is pinned to those vectors and the authors' SipHash-2-4
vectors, and CsCheck compares the implementation with it for arbitrary keys, UTF-16 text, isolated surrogates and
splits, and for text around the 0x7F/0x80 boundary in both encodings at every prefix split. Growth and append after
lookup are covered through `KeyBuilder`. Deterministic Aa/BB polynomial-collision families, long common prefixes,
NUL and isolated surrogates are assessed using bounded equality-chain work, not fragile nanosecond assertions.
These cases are regression evidence; their distribution alone is not the security argument. Forced-collision engine
tests cover every key path, concurrent publication, removal and frequency/eviction behavior.

A per-thread memo retains at most one immutable string of at most 2048 UTF-16 code units and its hash.
It checks **reference identity**, so another owner is independently hashed; larger keys are not retained.
It only avoids hashing the same owned key again during lookup/publication/eviction. The process key
never changes, and test-only collision masks are applied after this memo. It is not a value cache. Nodes retain that full randomized 32-bit hash for the frequency sketch and
reuse it for removal. This replaces the former independently randomized BCL frequency hash; frequencies,
sampling, sketch sizing, eviction and expiration algorithms are unchanged. Dictionary collision masks never
apply to the sketch. As before, randomized bucket placement can differ across process starts; exact per-key
admission choices are not a portable identity or contract. Hit-rate workloads must still pass the gate.

An earlier SipHash-2-4 candidate failed the section-10 timing gate (see design-prefix-preflight.md). This
implementation differs: one compression round and three finalization rounds instead of two and four, the prefix
state is finished in locals instead of copied, and whole code units are packed instead of fed byte by byte. Its
section-10 results are recorded in its pull request.

### HTTP string table

The Core HTTP body table uses the runtime ordinal string comparer and its built-in collision defenses instead of
the generated-method prefix comparer. HTTP keys are already complete SHA-framed text, and no generated builder
uses that table. The string/span/builder fallback paths are model-tested; TinyLFU frequency hashes still use the
full process-secret hash. Generated method tables and their collision-defense argument above remain unchanged.
