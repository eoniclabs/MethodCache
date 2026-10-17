# Review of design-series integration

Johan approved the combined review arrangement on 2026-10-17. PR #134 will be reviewed against main with commit history preserved, after the running final timing study completes. No retarget, rewrite, merge, closure or release has yet been performed.

Current combined revision: `c9ec0db33d1d5b27a68ff7a31d7437c603b360a5`. Current main: `8402ad2f70f4a6569ed081ba928ddfd4ebcb52f7`.

All twelve other open design PR heads are ancestors of the combined revision. The current main is NOT an ancestor. The combined branch therefore preserves the complete commit history and corrections; a squash would discard that review ancestry.

## Approved review arrangement; final gate remains required

Review the complete combined diff against main and integrate it as one change, preserving commits with a merge commit. Keep the earlier PRs and their failed cost results as review/history references. Do not merge the failing historical heads sequentially as if their individual cost gates passed. The explicit integration decision replaces separate historical-head cost acceptance for this series. Cumulative success is still not evidence that every earlier PR passed.

The alternative of backporting fixes and rewriting the entire stack was not selected. Historical PRs remain review/history references; no historical measurement is relabelled.

## Preconditions

- All current production behavior and the 76 acceptance rows retain source-specific evidence.
- All required current-head CI jobs pass: verified c9 run with 2,639 passing Linux tests, zero failures, one intentional opt-in skip and 94.3% diff coverage.
- Cumulative 67-case cost profile passes, with six unchanged approved exceptions.
- Final cumulative paired-time gate remains open; inactive proposed limits are not accepted.
- The approved combined arrangement resolves the historical per-change merge constraint; actual combined cost and timing acceptance remain required.
- No package publication or PublicAPI promotion is included.

## Current main integration check

Main contains the squash commit for #136, whose branch version is already included in the design history. `git merge-tree --write-tree` returned 0. The prospective merge tree is byte-identical to the current combined tree. This object-only check changes no branch or checkout and performs no merge.

The approved arrangement is recorded in §10. It authorizes combined review against main, preserves history and replaces historical-head cost acceptance for this design series. It does not authorize merging or new time exceptions.
