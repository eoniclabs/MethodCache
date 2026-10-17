# Final design acceptance inventory

[acceptance.json](acceptance.json) maps all76 specification rows to their required
outcomes, inspected source assertions and original validation reports. The approved
performance gate now passes; final approval-publication CI remains required.

Johan approved combined review in#134 againstmain with commit history preserved,
replacing separate historical-head cost acceptance for this series. All17historical
cost failures remain recorded. On2026-10-17 he approved alternative1: retain the
HTTP design with five exact reasoned time caps. No other gate is relaxed.

[Canonical acceptance replay](../http-time-acceptance-v1/) covers all53existinglocal
CPUcases plus6affectedprefixmisses:59completecases,5acceptedtimecosts,0unaccepted
slowdowns,0wideintervals and0missingcases. Twelve current Mac HTTP cases and two
complete precise Nimbus cases cover the14affectedHTTPcases separately. Original
fullMac14retainsits2wideintervalsandfailedverdict; no studies are pooled. Allactual
source points,source-impact qualifications,raw measurements and strict failures remain.

The cumulative cost profile passes all67cases with0failures and6unchangedaccepted
costs. New time rules accept no bytes or commands. F05 prefix acceptance and filtered
SipHash CPU evidence keep their own actual sources and the prior exact int limits.

[Exact7bfunctionalcheckpoint](../combined-functional-7b313db/) passes all14jobs and
fourstatuses:2639Linuxtests,0failures,1intentionalopt-insoakskip,94.3%diffcoverage,
99randomproperties,enabledsoak,cultures,requirednativebackends,AOT175,sevenpackages,
add-ons,demoanddocs. The new rules/docs publication requires its own final CI.
No merge,release,PublicAPI promotion or PR138 integration is included.
