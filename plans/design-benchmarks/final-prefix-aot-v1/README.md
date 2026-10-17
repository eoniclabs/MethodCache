# F05: explicit JIT and Native AOT prefix execution

All five verification phases pass: JIT execution, formatting, native publish,
native execution and SDK receipt. The new smoke cases execute short/long keys,
int/GUID arguments and sync/Task/ValueTask hits and misses with listeners off/on.
Each of 24 shape/flag cases checks two independent keys, resident hits and exact
origin-call counts; listener-enabled execution also checks all 48 requests.

The actual native compiler marker and executable checksum/type are retained.
All 20 frozen vendor image/manifest/source files match their original freeze
commits. The runtime source is the existing combined abd0eae candidate; only the
two recorded AOT test sources are added. This is execution/compatibility evidence,
not a time comparison. Final published-head CI remains required.
