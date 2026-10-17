# Final generated-prefix cost profile

The complete [48-case comparison](cost-results/report.md) passes with zero failures,
zero execution errors and zero accepted exceptions. Both sides use the same frozen
harness and unchanged accepted limits: `f43854dbcd3646893f490e78a0a1d9651e9c4521`
against `abd0eae840e9b277358ca643a1d4ddb3d56653cf`.

The cases cover sync/Task/ValueTask hits and misses, short/long prefixes, listeners
off/on and int/GUID arguments. All 24 hit cases have unchanged allocation costs.
This includes the existing long-key materialization cost; this profile does not
claim that arbitrarily long keys allocate zero bytes.

The input/preparation hashes, both common source copies and all 444 catalogued top-level build-file
hashes were independently verified before measurement and again after completion.
Only this cost phase is complete here. The separate 28-case paired-time cohort
and the full final time gate remain open; their results are not inferred from this
cost pass. The binary catalogues are checksums, not included executable files.
