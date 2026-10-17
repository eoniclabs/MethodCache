# Acceptance inventory: final gate is open

[acceptance.json](acceptance.json) covers all 76 explicit §11 rows, with each
required scenario/outcome, inspected source assertions and exact file hashes.
It is an evidence inventory, not a pass inferred from test names or CI totals.

The last complete published functional checkpoint is abd0eae (all fourteen jobs,
four statuses,94.0%diffcoverage). The candidate additionally has the explicit
native-prefix checks, ordinary-late-DI control, macOS scheduling tests and the
two-line saved-node-hash correction. Their local/isolated evidence is identified
separately; full CI on the new published commit remains required.

The complete original full71, prefix28 and quiet3 time reports are retained as
failed. The candidate cost profile passes all67caseswith6unchangedaccepted costs,
and independent Nimbus/Mac comparisons confirm the miss correction. Nimbus GUID
sync remains precisely slower and other original intervals remain too wide.
The unadopted inlining trial clears no gate. Only Johan's two exact no-listener
sync/int SipHash limits are accepted; no GUID/miss or precision waiver is added.

Changed source hashes are updated only for the independently checked AOT program
hook and the two reasoned int time limits; exact prior hashes remain recorded.
The candidate memory-engine/immutable-node/hash-memo implementation and new
acceptance test sources are explicitly listed with their current hashes.
