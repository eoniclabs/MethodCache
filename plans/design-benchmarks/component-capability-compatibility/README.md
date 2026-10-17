# Component capability compatibility

The previous prefix harness recognized `MarvinState.cs` only. Against SipHash it
silently compiled `PrefixLookup` as a full-span lookup. Existing value assertions
still returned 42 and could not detect the wrong measured path.

An independent reflection check was added before repairing detection. The actual
original helper run then failed (`sip-red.log`). The repaired helper detects the
hash and in-place constructor separately, selects their paths at compile time,
and checks the resulting choices against the referenced Core binary. All four
variants pass the same setup Program and byte-identical three C# workload files:

| Runtime | Hash path | Key construction | Result |
| --- | --- | --- | --- |
| PR5, 2f4a91d | Complete span | StartKey | Pass |
| Prefix, 9c31e3b | Marvin saved prefix | StartKey | Pass |
| Constructor, 2b3cb93 | Marvin saved prefix | Direct constructor | Pass |
| SipHash, 83b92c6 | SipHash saved prefix | Direct constructor | Pass |

Each run checks all three lookup lengths, eight prefix/buffer/listener setup
combinations, 100 cached calls per result shape, exactly one origin load, buffer
growth and all four reference-cache paths. The direct constructor stays in the
benchmark local; no helper returns the key builder by value.

A separate sensitivity run removes both prefix/hash symbols from the corrected
SipHash harness. It fails with the capability mismatch despite compiling and
returning resident values (`sip-disabled-prefix-red.log`). A permanent benchmark
setup fact also compares compiled selections with the Core binary.

Local .NET 10 verification: 92 benchmark tests pass; 1,036 Core tests pass with
one intentional opt-in soak skip; all 31 Python validation tests pass. The actual
benchmark project builds and selected C# whitespace verification passes. Full
combined three-platform CI remains required.

These are preliminary int workloads used to verify compatibility, not the final
benchmark source freeze. Claude's GUID workload update must be integrated and
verified identically across revisions before timing. No timings or speedup claims
are derived from these checks. Existing int timing diagnostics stay separate.

Per-runtime manifests record exact runtime heads and common workload checksums.
Their workload Git HEAD identifies the base revision; the common-source snapshots
and digests identify the uncommitted compatibility edits actually compiled. The
outer manifest retains original and LF-normalized log/source SHA-256 digests.
