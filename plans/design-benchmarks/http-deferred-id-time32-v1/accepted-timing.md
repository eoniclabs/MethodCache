| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 10464.6 | 9928.6 | 0.944 | 0.930-0.958 | faster |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 10633.4 | 9905.8 | 0.930 | 0.923-0.937 | faster |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 10688.5 | 10063.6 | 0.942 | 0.933-0.952 | faster |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 10695.7 | 10046.8 | 0.937 | 0.926-0.949 | faster |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 5371.2 | 5414.2 | 1.010 | 0.991-1.029 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 5378.7 | 5353.7 | 0.998 | 0.982-1.014 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 5529.0 | 5509.7 | 0.998 | 0.988-1.008 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 5561.7 | 5533.0 | 0.995 | 0.975-1.016 | same |
