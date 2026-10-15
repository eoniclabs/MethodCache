# Independent-run precision follow-up

The CPU-affinity baseline pilot retains independent process launches, default
JIT/features, concurrent workstation GC, 16 warmups and 12 measured 500 ms
iterations. Its first two GenericHit means already imply that no positive third
mean could bring the relative three-launch 95% half-width below 7%. The compact
calculation and frozen source manifest are retained here. These are diagnostic
baseline results, not a source-change comparison or performance acceptance.

A fixed nine-launch GenericHit precision study is therefore planned before
examining source-change results: keep all three original launches and add six
independent launches with the identical source/job/affinity. Other pilot cases
retain their three-launch scope. Every batch still owns an exclusive nonblocking
Nimbus capacity lease for at most 25 minutes; no running CI is interrupted.

The report helper now accepts complete fixed series of 3, 6 or 9 launches,
using two-sided 0.975 Student-t quantiles with 2, 5 or 8 degrees of freedom.
An explicit exact-method filter permits a supplementary series for one case;
unselected cases are not assessed by that report. Missing launches, missing
interference guards, missing selected cases, unequal base/change launch counts,
duplicates and incomplete matrices remain errors. Wide intervals never pass.

New collection/count tests fail against the prior three-only helper; all 25
statistical/activity guard cases pass after the change. This is reporting
capability, not a shipped-runtime change or an accepted timing result.

The statistical method/table is described by
[NIST](https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm).
