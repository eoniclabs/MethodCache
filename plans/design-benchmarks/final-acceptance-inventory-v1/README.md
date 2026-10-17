# Acceptance inventory: timing gate remains open

[acceptance.json](acceptance.json) covers all 76 explicit specification rows with
required scenarios, inspected source assertions and exact file hashes. This
inventory distinguishes functional verification from outstanding timing gates.

Published checkpoint `3e9df33eb9da29c7740b2cd4434345a4486a62ce` passes all fourteen
jobs and four statuses, with 2,612 Linux tests and 94.0% diff coverage. It includes
the node-hash correction, explicit native-prefix execution, ordinary late-DI
control, portable proof paths and the deterministic test-support corrections.
The evidence-only documentation batch will need its own published-head CI.

The complete original timing reports remain failed/noisy. The corrected runtime
passes all 67 cost cases with the same six accepted costs. Independent cumulative
comparisons confirm the miss correction; Nimbus GUID sync remains slower. A
diagnostic isolates hash/lookup as the relevant mechanism and does not change
production or grant acceptance. A fixed 53-case quiet-Mac cohort and thirteen
affected F05 cases remain pending. Only the two previously authorized exact
no-listener sync/int time limits are currently accepted.
