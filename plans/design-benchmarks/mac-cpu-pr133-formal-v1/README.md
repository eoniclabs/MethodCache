# PR133: complete CPU acceptance after precision reconfirmation

All five declared cases have ten complete alternating pairs: the three GUID
return shapes and long-prefix int retain their complete original measurements;
the entire short-prefix case uses its independently complete precision
reconfirmation. Each case is analyzed independently. Original raw studies and
their initial verdicts remain available in the neighboring original and
short-v1 directories; no individual run is selected or pooled across studies.

The [original-rule report](original-rules.md) has two confirmed sync/int
slowdowns and no noisy cases. The [accepted-rule report](accepted-rules.md)
passes: three GUID improvements and two intentional SipHash costs within 8%.
Johan authorized that security/performance tradeoff; the two exact time limits
are now reviewable code. Existing byte/command limits are unchanged, and time
rules cannot accept those metrics or waive the 6% precision requirement.

This is PR133 versus its Marvin base, not cumulative acceptance versus f43854d.
The original five-case and complete one-case evidence each retain all actual
measurements, source and build catalogues, runtime/GC and quiet observations.
