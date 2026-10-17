# GUID-workload macOS history failure

PR135 head `419c786d949d361cd2acd418178e4b0a8e21a860` fails its macOS build in
Nerdbank.GitVersioning before unit tests run. Linux and Windows statuses pass; aggregate and macOS fail.
The native Git connectivity check on that exact retained CI clone confirms missing reachable objects,
including tree `82031f54dbd35dd1ddba1f06211db686bb3cafb2` referenced by ancestor commit
`7738715e03aad9633894647c141ecb6f37837ff1`. This is not solely a managed Git reader observation.

The current CI mirror passes its own native connectivity check. That later healthy mirror does not
explain how the earlier clone became incomplete. No repair or forced rerun is claimed; original logs,
failed native Git output, exits and exact statuses are preserved. The failed checkout and mirror were
inspected without modification. Latest combined-head CI remains required.

Log manifests retain original and normalized stored digests. No benchmark measurements are inferred.
