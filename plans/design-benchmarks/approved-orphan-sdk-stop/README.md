# Approved old SDK cleanup

On 2026-10-16 Johan explicitly authorized stopping only the old SDK container from run
`20261015-231740-b92553dac51d`, leaving Redis, SQL Server and other jobs untouched.
Before stopping, its full container ID, exact run label, running state, diffcoverage command,
live Redis testhost and absent completion file were verified. The latest log and private
heap diagnostics remain private on Nimbus.

Only that SDK container received Docker stop. The recorded Redis/SQL container running states and
start timestamps match before and after. The old CI diffcoverage result became exit 137; its failure
is preserved. Its orphan shell/testhost ended and released its shared capacity lock. The existing
linux.sh EXIT cleanup subsequently removed that old run's own temporary backend containers, as its
run-label filter does normally. No direct stop/removal was sent to a database container. Another ordinary
CI pass then held the lock and was left running; services, leases and unrelated jobs were not changed.

Docker accepted the planned --time argument but reported it deprecated in favor of --timeout.
Future equivalent approved cleanups should use --timeout, after revalidating exact ownership.
No benchmark timing acceptance or full-suite pass is implied by this cleanup.
