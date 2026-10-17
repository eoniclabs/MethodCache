# SipHash macOS checkout observation

The macOS run for SipHash code head 9338454984a436e64ba5e49e8fcb371419cb12ea
failed with exit 128 before build or tests started. Its original console contains
`fatal: unable to read tree (13157bec0f7ddfd77e89b0bb28ffc21476822aa8)`.
The cause is unproven. No cache repair, infrastructure change or forced CI rerun
is claimed. This is not a failed functional test and does not establish a pass.
The later combined head needs fresh three-platform CI.
