| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 3579.5 | 3770.0 | 1.051 | 1.045-1.058 | accepted: Approved by Johan on 2026-10-17 (alternative 1): bounded aggregate cost of the shared local HTTP store, full storage metadata and conservative resident-byte accounting. The complete quiet-Mac comparison measures +5.1% (95%1.045–1.058). This is a conscious feature/cost tradeoff, not proof that every extra nanosecond comes from one field. No ordinary-hit,method-cache or native-L2 limit changes. |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 5496.8 | 4308.6 | 0.786 | 0.778-0.793 | faster |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 5451.3 | 4317.2 | 0.787 | 0.778-0.797 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 6799.7 | 3822.6 | 0.560 | 0.549-0.571 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 6795.5 | 3811.5 | 0.559 | 0.555-0.564 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 6829.0 | 3831.8 | 0.568 | 0.547-0.590 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 6794.0 | 3855.9 | 0.572 | 0.562-0.582 | faster |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 2044.1 | 2103.3 | 1.030 | 1.021-1.039 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 2063.9 | 2108.0 | 1.023 | 1.015-1.031 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 2056.3 | 2132.0 | 1.041 | 1.032-1.049 | accepted: Approved by Johan on 2026-10-17 (alternative 1): bounded aggregate cost of enabled Core origin instrumentation and shared HTTP residency bookkeeping. Preserve request/load labels,duration/error records and activity parenting. Complete quiet-Mac measurement +4.1% (95%1.032–1.049). No no-listener revalidation or ordinary-hit limit changes. |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 2050.4 | 2159.0 | 1.067 | 1.040-1.095 | accepted: Approved by Johan on 2026-10-17 (alternative 1): bounded aggregate cost of enabled Core origin instrumentation and shared HTTP residency bookkeeping after invalidation. Preserve duration/error labels,activity parenting and generation guarantees. Complete quiet-Mac measurement +6.7% (95%1.040–1.095). Bound is on the paired estimate; the unchanged6%precision check still applies first. |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 1279.2 | 1228.5 | 0.960 | 0.954-0.967 | faster |
