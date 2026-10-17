# G05: explicit ordinary late registration control

The existing late-cacheable registration fact also adds an ordinary string service
following `AddMethodCache`, builds that valid container and verifies both the late
service and existing cached decorator resolve. It then retains the original error
assertion after adding a second, undecorated cacheable registration.

This closes an explicit acceptance-test gap without changing production behavior.
All 19 Quick Start facts/theories pass on net10.0, and formatting passes. These are
local receipts; the final published commit still requires normal CI. The manifest
records exact source and original/stored log hashes. No runtime or timed workload
changed, and no pre-fix production failure is claimed.
