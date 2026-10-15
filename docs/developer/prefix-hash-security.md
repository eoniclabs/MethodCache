# Prefix hashing review

The prototype uses SipHash-2-4 with a fresh 128-bit process-secret key from `RandomNumberGenerator`, hashing exact
UTF-16 little-endian bytes. Each immutable invocation snapshot holds the unfinished state after its prefix. Lookup
finishes a copy after the current suffix. String, span and fallback paths use the same algorithm and secret. All
comparisons still check the entire text ordinally. No seed/state/hash is serialized or used as an external identity.

SipHash was designed for adversarial hash-table inputs. The [authors' specification](https://www.aumasson.jp/siphash/siphash.pdf)
and [reference implementation/security statement](https://github.com/veorq/SipHash) define the 2 compression / 4 finalization
rounds and secret-key requirement. A random seed on a fast unkeyed hash is insufficient; no claim relies on random
input distribution or BCL's collision-triggered rehashing. The custom comparer does not receive that BCL protection.

The 64-bit PRF output is folded to the dictionary's required 32-bit hash. This does not give 128-bit collision
resistance: ordinary birthday collisions in 32 output bits remain possible. Its purpose is to prevent an attacker
without the secret from cheaply preparing a large reusable collision family. An adaptive online oracle, excessive
request volume, enormous key text, or a compromised application can still consume resources. Application admission,
authorization and request size limits remain necessary; the cache's entry/weight bounds still apply. Process secrets
and unfinished states must never appear in logs, administration, telemetry, storage or wire frames.

Validation includes the authors' 64 pinned reference vectors; CsCheck across arbitrary byte and UTF-16 split points,
including isolated surrogates; buffer growth and append after lookup; and forced collisions in an individual engine.
The immutable test-only comparer mask never changes process-global state. Adversarial cases include embedded NUL,
long shared prefixes and structured suffixes; work is assessed by bounded hash-chain comparison counts rather than timing asserts.

This is an implementation candidate. Functional, frozen-vendor/AOT, backend identity and section-10 performance gates
must pass before treating the optimization as complete. Security is not traded away for a faster sub-benchmark.
