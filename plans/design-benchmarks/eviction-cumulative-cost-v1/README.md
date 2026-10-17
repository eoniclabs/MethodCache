# Cumulative cost profile after the node-hash reuse correction

All 67 cases run on each side, with zero execution errors, **zero cost failures
and the same six accepted costs**. The independent local review reproduces the
terminal remote verdict with byte-identical accepted.json. No byte, command or
time bound is added for the correction.

Base is `f43854dbcd3646893f490e78a0a1d9651e9c4521`; head is
`abd0eae840e9b277358ca643a1d4ddb3d56653cf` plus the exact two-line node-removal
patch, SHA-256 `8829ce3c5f5c3bd5b6b09e0678dc22abc540e39f2672d2f1dc129f2f2e64055a`.
Both sides use identical frozen measured sources and the pinned SDK container.
All 222 catalogued build files verify before and after measurement. Fresh Redis
and SQL Server containers use their exact retained image digests and own labels,
inside one bounded shared-capacity lease; shared providers remain untouched.

[Every compared row](report.md), both raw sets of 67 records, original review,
controller, SDK-image/source/build receipts and checksums are retained. This is
cost acceptance, independent of the still-open GUID time regression.

Only inputs.json's binary dictionaries use reversible path/hash records. Ten
scanner findings were verified to be build-file checksums, then represented as
records without removing or changing any checksum. Run restore-original-metadata.py
with a new directory to reconstruct exact metadata and verify all seventeen
original indexed archive files. Original archive SHA-256:
`119b0d46fecb367019ce2089e03421a442099b3389ea3e9ab12810b3716b2bd2`.
