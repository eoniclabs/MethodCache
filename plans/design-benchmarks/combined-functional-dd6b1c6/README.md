# Exact combined functional checkpoint dd6b1c6

All fourteen CI jobs pass for `dd6b1c6a7b7ca47396d2ac4a06fd0df4332a45b0`, run
`20261016-085559-dd6b1c6a7b7c`. All three platform statuses and the aggregate status
are success. This is actual Linux/macOS/Windows evidence, including the strict
no-listener allocation probe after its one fixed complete warmup.

Linux runs 2,561 facts across sixteen projects with no failures and the one intentional
opt-in soak skip. Real Redis/SQL integration, properties, enabled soak, both cultures,
demo, Native AOT and all 175 signature cases, installed packages/add-ons and docs pass.
Changed-line coverage is 3486/3708 (94.0%). The preceding `6caa856` checkpoint also
passes all fourteen jobs; its original 388050f macOS 272-byte failure remains preserved.
The type and precise source of those historical 272 bytes are still unproven.

`manifest.json` pins the exact SHA, collection time, all job exits, statuses and both
original and LF-normalized stored log hashes. This is functional evidence for this
head. Subsequent analysis-tool/docs changes need their own CI, and no timing or
complete section-10 acceptance is inferred.
