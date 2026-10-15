# Independent-run precision follow-up

The CPU-affinity baseline pilot retains independent process launches, default
JIT/features, concurrent workstation GC, 16 warmups and 12 measured 500 ms
iterations. Its first two GenericHit means already imply that no positive third
mean could bring the relative three-launch 95% half-width below 7%. The compact
calculation and frozen source manifest are retained here. These are diagnostic
baseline results, not a source-change comparison or performance acceptance.

This first motivated a nine-launch GenericHit supplement, but it was stopped
before timing and replaced by the all-four-hit startup-affinity protocol below.
Earlier samples remain separate. Every batch still owns an exclusive nonblocking
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


## Completed pilot and replacement precision protocol

The complete v2 three-launch baseline pilot is retained in the archive and
summary. Relative 95% half-widths are Sync 12.31%, Generic 8.05%, Task 4.05% and
ValueTask 1.73%, all at zero measured bytes. Fixed child-process affinity alone
therefore did not provide adequate precision for several cases. No source
regression or performance pass follows from this baseline-only study.

The planned Generic-only supplement was stopped while its owned controller was
sleeping without a child, before any timing. It is replaced by a fixed nine-launch
study of all four hits. The v3 parent BDN host starts under `taskset --cpu-list 2`
before .NET initializes; children inherit that CPU and also export mask 4.
A setup probe must first confirm ProcessorCount=1 and Affinity=4 in the pinned
image. This makes startup CPU availability explicit; it does not diagnose the
cause of the v2 variance. All v2 samples remain separate and are not pooled.
The new study still requires interval review and establishes no product speedup.

[Microsoft documents](https://learn.microsoft.com/en-us/dotnet/api/system.environment.processorcount?view=net-10.0)
that ProcessorCount reflects affinity and is fixed at runtime startup. This is
why the parent launch setting and startup probe are part of the new protocol.

V3's first batch later completed four cases but its validator exited 1. The short binary display
was misread, and BDN's extra affinity-setting attempts failed. Those artifacts are retained
separately. Fresh V4 sets affinity only before startup and verifies actual OS CPU sets for each
executed child PID; its nine fixed baseline launches are pending. See
[the retained failure and V4 protocol](startup-affinity-validation/). No earlier samples are pooled.
