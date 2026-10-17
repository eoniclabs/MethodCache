# GUID and int hash-component preflight

The hash-only lookup now measures both the original :42 suffix and the canonical
GUID part used by MethodCacheKeys. Both suffixes are checked at all three prefix
lengths, with string/span/prehashed lookup returning the resident value.

Identical sources and setup Program pass against PR5 (no prefix hash, StartKey),
Marvin/StartKey, Marvin/direct constructor and SipHash/direct constructor.
The existing eight prefix/buffer/listener GUID facade setups retain one origin
load, repeated cached values and long-key growth; all four GUID memory-cache
reference paths pass. The expanded component matrix contains 54 cases: eighteen
hash lookups, thirty-two facade/key-build cases and four reference lookups.

These are local setup checks, not measured performance or a final Nimbus freeze.
The new GUID/int suffix parameter is explicit; previous :42-only results remain
separate. Common source snapshots, per-runtime capabilities, logs and digests
are retained here.
