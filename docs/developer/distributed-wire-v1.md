# Distributed contract 1

This contract is independent of the generated-code ABI and of value entry formats. Keep the checked-in
`tests/MethodCache.Core.Tests/Fixtures/wire-v1.json` vectors unchanged throughout 1.x. The frozen generator
vendor binaries in `CompatTests/Baselines/Contract1` are a separate compatibility fixture.

## Backplane envelope

Transport text is ASCII `MC1:` followed by Base64. Before decoding, reject encoded lengths exceeding the
4 MiB binary-frame bound. The binary frame starts with version (u8, 1), operation (u8: key=1, application
tag=2, method=3, clear=4), flags (u16 LE), and body length (i32 LE). Upper flag bits are mandatory and
unknown bits reject the frame; lower optional bits may be ignored.

The body is TLV: field (u8; bit 7 means mandatory, lower seven bits identify the field), length (i32 LE),
then bytes. Required fields are sender=1 (UTF-16 LE, at most 1,024 code units), target=2 (UTF-16 LE; omitted
for clear), and timestamp=3 (i64 LE Unix milliseconds). Duplicate known fields, negative/out-of-frame
lengths, odd UTF-16 lengths, missing required fields, unsupported mandatory fields/operations and invalid
timestamps reject the frame. Unknown optional fields are skipped after validating their bounds.

Each rejection clears local cached data and emits a payload-free warning once per provider instance.
This does not repair messages the transport never delivered. Key, tag, method and clear are separate
operations; application text, including NUL or a method-marker-looking tag, never selects an internal one.
SQL stores envelope version 1 in MessageType and the same envelope text in Target; row sender must match.

## Metadata and epochs

Logical key/tag/method/clear metadata keys contain neither value-format nor policy identity. Existing
method identity text encoding remains stable, preserving exact UTF-16 units. A normal timestamp is eight
signed LE bytes. An HTTP-marked timestamp is that stamp followed by version=1, 16 opaque initialization
bytes (the .NET Guid byte representation), and u64 BE revision; future trailing data is preserved.

Ensure initializes only missing epoch state. Advance increments every time; after overflow or missing state
it uses a fresh initialization ID. An old/retried Advance can increment again but cannot restore an older
ID. Redis compares/increments bytes in single-key Lua operations, including beyond Lua's exact numeric
range. SQL uses key/range update locks and two unsigned 32-bit revision halves. Updating a marked method
tag/clear stamp advances its HTTP revision in the same operation. Marked metadata has no TTL.

A scope's permanent retention contract is established on first use. A shorter node configuration is rejected.
Longer requests retain the existing common minimum, which caps all value deadlines; establishing a different
common bound requires a new KeyPrefix. Atomic maximum writes never shorten existing stamp lifetimes.

## Value format 6

Physical value namespace is `KeyPrefix + e6:s{SerializerVersion}:`, followed by the framed policy/logical key.
Other post formats use their own `eN` namespace. SerializerVersion is an independent application schema
identity; changing serializer settings/result schemas requires raising it or the method Version.

Header: version u8; kind u8 (null/value plus explicit-set bit); flags u16 LE; extension length i32 LE (0–4096);
load-start, first-publication, logical deadline, physical deadline as four i64 LE Unix millisecond values;
tag count i32 LE (0–10,000); extensions; length-prefixed UTF-16 LE tags; content-type length u8 and bytes;
serialized payload. Unknown mandatory flags and malformed frames become misses. Every read checks the
logical key tombstone, even for entries written by another post format. Native Redis/SQL providers batch
value and tombstone retrieval; fallback test stores retain separate per-operation timeouts.

Deadlines are bounded from source start by common metadata retention minus skew. Physical store expiry
is absolute, so delayed serialization/transport never extends it. Round-trip checks are enabled by default,
can be explicitly disabled, and do not prove nested polymorphic/private data preservation.
