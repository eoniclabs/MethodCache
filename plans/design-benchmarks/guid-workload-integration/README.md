# GUID workload integration

Integrate PR135 head 0c09fc1d36c68a87fb7d55b414b8616f3082de05 on the combined
stack. Sync/Task/ValueTask/generic contract ids and the collection now use GUIDs;
miss GUIDs come from a counter, without adding random-number generation. The
README comparison retains both int and GUID workloads as requested. The separate
constructor/StartKey selections survive the PrefixHitPath merge.

All 94 benchmark tests pass locally on net10. The identical three component source
files and setup Program pass against PR5, Marvin, constructor and SipHash variants.
GUID facade and reference-cache cases retain their expected values, one origin
load, long-prefix growth and listener coverage. The standalone hash lookup still
uses its explicitly unchanged :42 suffix; it is not a GUID hash measurement.

Selected whitespace verification exits zero with a workspace-loading warning.
The manifests and common snapshots identify the exact merged bytes compiled,
including uncommitted workload edits relative to their recorded base heads.

Final source freeze is still pending: validate actual generated GUID hit/miss
origin counts and rebuild/verify all full-series revisions with common sources.
The new catalog must be regenerated after removing the redundant GuidHit entry;
old 64-case evidence is preliminary and must not be attributed to the new source.
No earlier int timing is pooled with GUID results; no speed claim is established.
