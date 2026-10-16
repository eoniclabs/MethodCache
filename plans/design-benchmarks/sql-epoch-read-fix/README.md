# Read initialized SQL epochs without rewriting them

The complete cumulative comparison reports SQL HTTP fresh responses at
0.4153→13.6002 ms. Source review identifies unconditional UPDATEs inside every
EnsureEpochs call, including reads of already initialized permanent records.

A real SQL regression installs an UPDATE-rejecting trigger in its own unique
test schema after initializing two epochs. The repeated read fails before the
fix, proving the unnecessary mutation without a wall-clock assertion.

The correction keeps the sorted serializable locks and one-command vector read.
Canonical existing permanent metadata is returned without UPDATE. Missing/expired
records, old stamps, finite absolute expiry and sliding metadata still initialize
or normalize atomically. Two boundary cases check finite/sliding repair to maximum
retention and then forbid updates on the subsequent read.

All four epoch contract cases, all 66 SQL integration facts and all 42 SQL unit
facts pass against actual local SQL infrastructure, with no infrastructure skips.
Each trigger/table/schema belongs only to that test. Other services/jobs are not
stopped or changed. The original cumulative result and running historical cohort
remain unchanged.

This proves correctness and removal of redundant writes, not timing acceptance.
The candidate still needs a frozen same-machine performance comparison and its
published-head CI. `manifest.json` retains original/stored log hashes.
