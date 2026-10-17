# Lookup-helper inlining trial: not adopted

This isolated experiment compares the verified node-hash reuse candidate with
that same source plus replacing NoInlining with AggressiveInlining on the three
public KeyBuilder lookup helpers. Both start at
`abd0eae840e9b277358ca643a1d4ddb3d56653cf`; base includes the two-line node-removal
patch, and head includes the retained combined runtime.patch. Source/patch and all
222 build-file checksums verify before the fixed comparison. Candidate Core tests
also pass 1,045 tests with one intentional opt-in soak skip during preparation.

All three return shapes and ten alternating pairs are retained: 60 launches,
same measured C# sources, .NET 10.0.12, workstation GC, actual affinity and
interference checks. [The complete report](timing.md) has three overly wide
intervals and fails. Sync GUID's estimate is only 1.7% faster, while Task and
ValueTask estimates are 7.5%/8.7% faster; none is sufficient evidence to adopt
the change. **The helper attributes remain unchanged in the implementation.**
No case is selected, no original result is replaced and no time limit is added.

Only prepared.json's binary dictionary is represented reversibly as path/hash
records. Run restore-original-metadata.py with a new directory to reconstruct
original metadata and verify all 151 indexed archive files before integrity.py.
Original archive SHA-256:
`f2e5b4f9424179607c296f12f8482dd1c3800cfe67e3ab2fbde2c3b0757b3da1`.
