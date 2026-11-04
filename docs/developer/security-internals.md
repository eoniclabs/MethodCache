# Security internals

For contributors. The user-facing threat model is in [docs/security.md](../security.md); this page records the
implementation-level guarantees behind it, and the boundaries of the build and release infrastructure.

## Self-hosted CI pilot

The opt-in Actions pilot uses a dedicated Nimbus account and a restricted organization runner group.
MethodCache is public: neither the caller nor the reusable workflow permits pull-request events. Only
trusted pushes/manual dispatches on the reviewed main/bootstrap branches can reach the job; checkout
uses that event's commit, with read-only token permissions and credential persistence disabled.
The external `MethodCache CI` merge gate remains independent.

The account's Docker access is root-equivalent. A separate account is not a sandbox, and runner groups
and event guards do not protect against a malicious repository writer. Do not add untrusted forks,
production secrets or signing credentials. Disposable execution isolation is required before widening
that trust boundary. The host capacity lease prevents overlapping heavy CI, not hostile code execution.
`build/ci/tests/test_actions_linux.py` verifies admission, lease lifetime, missing-lock refusal, and
failure/timeout propagation; the workflow event/ref gates are part of security review.

## Release publishing

`build/ci/publish.sh <tag>` retrieves only the configured BWS publishing secret after packing. The
NuGet API key goes to a .NET SDK 10.0.300+ CLI through `NUGET_API_KEY` and `NUGET_SYMBOL_API_KEY`, without appearing in command-line
arguments or shell traces. The BWS token is removed from the upload process's environment; inherited BWS
and both NuGet publishing credentials are removed from build-step environments. The script refuses a release
whose `MethodCache CI` status is not green. `build/ci/tests/test_publish.py` covers these boundaries and
failure paths. The host, release code and SDK remain trusted: this does not isolate malicious code running
under the same OS user from local credential files.


## CI checkout collection and other tested guarantees

CI checkout collection and result reuse have a separate boundary from application caching. The collector only
removes coordinator-owned idle macOS clones with verified origin/full commit, terminal job results, and no
source changes or unexpected local data. It refuses redirected roots/results and keeps active checkouts and
unknown directories. Reuse requires the full SHA and all expected valid job exits; partial archives, failed
results and symlinked exit files cannot become a passing status. The regression suite under `build/ci/tests`
tests these protections. Build jobs still execute repository code as the build-server user; the collector is
not a sandbox for malicious pull requests.



MessagePack key identity includes derived state in nested base-class/interface slots and collections. `PolymorphicKeyReviewTests` checks that distinct derived argument data cannot share a cache key.

HTTP age arithmetic saturates during response correction and resident-time accumulation, so an extreme upstream Age value cannot overflow the freshness calculation. `ResponseDelayReviewTests` covers these input boundaries.

Bulk publication guards are tested during key, tag and clear invalidation; in-flight batch results cannot resurrect invalidated entries. Collection-state properties and the generator signature corpus cover tenant/scope state on collection subclasses.

Persistent SQL insert locks, including other scopes sharing the table, cannot hold back newer invalidations or other committed gaps. The non-skipping `LongTransactionReviewTests` and `LockedGapReviewTests` in `MethodCache.Core.Tests/FaultInjection` exercise the production gap-discovery protocol without a database. Linked SQL integration variants additionally inject real locks through repeated polls and verify late commit/rollback recovery.

## Generated contract and registration boundaries

Generated code uses a versioned facade and captures policy, prefix and local publication generation together.
A key builder from another binding/snapshot is rejected before lookup. Retired loads cannot publish or serve
an old-generation fail-safe value. Descriptor/module state is separate from each container's live policy,
keyed binding and background scope. Unknown contracts fail before descriptor/factory execution; ambiguous
modules, candidates and unsupported cacheable bindings fail instead of choosing an implementation implicitly.
The corresponding tests are GeneratedContractTests, QuickStartRegistrationTests and the semantic signature corpus.

Policy-isolation tests reject application keys/tags that imitate internal method or namespace identities. Declarative
fingerprints use invariant length-framed UTF-16 and SHA-256; runtime namespaces use 16 random bytes per behavior change.
Typed method messages retire data without accepting policy from the sender. L2 reads enforce first-publication deadlines,
null/result predicates and original load-start tombstones, including after delayed transport completion. Format-5 malformed
header/truncation tests exercise the current parser. `PolicyBackendContract` runs the same assertions on Redis, SQL Server and PostgreSQL.

Generated methods always retain their method/service/type scope; the raw-key escape hatch is removed. Dynamic tag arguments use canonical scalar key formatting, keeping null, escaped strings and full-precision temporal values distinct. Reserved metadata keys and tags remain centrally framed and cannot be selected by caller-controlled strings.

Wire v1 rejects malformed lengths, mandatory flags/operations and unsupported versions before identity decoding.
A delivered incompatible frame clears local entries and warns once without its payload. Optional fields are bounded
and ignored. Entry deadlines are absolute and capped by the scope retention floor; logical key tombstones outlive
all entry formats and policies. Permanent HTTP epochs use atomic revisions with fresh initialization identities
after metadata loss or overflow. SQL initialization rejects unversioned/unknown schemas; use the delivered offline
migration for the known legacy layout. The roundtrip check is optional and cannot detect semantic losses in nested
derived or private state; explicit serializer configuration remains the application's responsibility.

Generated-method dictionaries hash keys with SipHash-1-3 under process-secret 128-bit keys (the keyed hash Python and Rust use for their hash tables): ASCII keys as one byte per character, any other key as UTF-16, each under its own key. Full key text is always compared
ordinally. Hash state is immutable per invocation snapshot and never becomes an L2/wire/admin identity. The
[algorithm and threat review](developer/prefix-hash-security.md) records the 32-bit dictionary output limitation,
reference sources and validation. `PrefixHashPropertyTests` pins published SipHash vectors in both encodings, arbitrary UTF-16 splits and append
invalidation; `PrefixCollisionTests` forces collisions through all lookup/publication paths and checks eviction.

The optional cross-platform Actions pool runs only reviewed main/manual code. Its macOS runners use
the existing login account and therefore share that account's filesystem and credential access; they
are trusted build hosts, not an isolation boundary. Baloo runs as the limited `bldr` account. Admission
wrappers cooperate with both external CI coordinators through OS-owned leases; locks limit concurrent
build load and do not restrict a job's host permissions. Private projects need an explicit trust review
before joining the reviewed public pool. No production/signing secrets are added by runner setup.
