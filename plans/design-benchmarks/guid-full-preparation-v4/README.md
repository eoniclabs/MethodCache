# Full GUID cohort: completed preparation and actual timing dispatch

All fourteen primary runtime points and six component points finish preparation on Nimbus.
All sixty compiled family inventories pass the actual immutable-input preflight. An additional
review rehashes 2,358 harness DLLs, all workload/program inputs and seventeen native-runner DLLs;
checks exact source revisions and clean runtime sources; and checks all sixty distinct successful
preflight rows. This is preparation evidence, not a timing or section-10 pass.

Primary cases: 27 local-contract, 20 local-pressure, 16 native. Each component point has
18 lookup, 36 facade/reference and 48 genuine generated-interface cases. Int and Guid labels
remain distinct. The fourteen exact SHAs and eleven actual comparison edges are retained.

The new native runner starts the pinned Redis/SQL images and verifies actual owned container
image IDs before and after the setup workload. The native workload exits zero; both verification
timestamps and image IDs are in `series/native-image-setup.json`. Future native timing launches
must produce their own before/after receipts. No unrelated backend is stopped.

- Frozen timing protocol SHA256: `047c51a87c0437a40d005bf11b66b226db6cd6114bd170f45fc1a6acc705ba60`.
- Prepared manifest SHA256: `d7ac2434c52a0e97efe4821a27b78244ab013c90eb8d471f7d6bf2bacad60166`.
- Sixty-family validation SHA256: `5ce4491add94e82d853d4dc67635a267028669128e21368641ef31ed43c68cde`.
- Independent review: 2026-10-16 08:30:39 UTC, before any timing starts.
- Explicit dispatcher launch: 2026-10-16 08:30:54 UTC, PID 4017740, verified live with the
  first exclusive lease acquired. This attempt subsequently fails before executing benchmarks because
  BDN cannot compile the asynchronous cold-HTTP iteration setup. Zero launches succeed.
  [Original failure and correction evidence](../guid-full-bdn-build-failure-v4/).
  This prepared cohort cannot supply timing acceptance; a fresh corrected cohort is required.

The fixed series uses three independent process launches per revision/family with descending,
ascending, descending revision order. Full workloads inherit all sixteen CPUs before CLR startup;
component families inherit CPU2. Each actual host and benchmark child must have the expected
observed OS affinity. SDK 10.0.401/runtime 10.0.12, concurrent workstation GC, sixteen warmups,
twelve Actual measurements and 500 ms target iteration time are fixed. Every exclusive batch is
bounded to 25 minutes and releases the shared capacity lock between batches.

All 227 retrieved original preparation files were SHA256-verified before text normalization;
`files-sha256.json` records original/stored hashes. Parent V2/V3 completed markers and driver logs
preserve the preparation chain. Harness C#/project/manifests and native-runner source are retained;
compiled DLLs and the source bundle remain on the measurement host. No timing results are included.
Use the corrected repository `validation/summarize-design.py` for later feature-equivalence review;
the older frozen measurement protocol and archived helper remain unchanged.
