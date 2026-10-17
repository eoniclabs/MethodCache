# Focused acceptance schedule

This schedule supersedes the breadth and ordering of the V5 schedule, not its
measurement settings or acceptance criteria. It was selected from source changes
before reviewing any V5 timing means. Completed V5 launches remain immutable and
may be reused only as whole launches with the same workload, runtime, configuration
and guards. Unselected launches remain available as diagnostic evidence.

## Coverage

Every original PR comparison and the cumulative comparison retains three
independent launches of every selected family at each endpoint. All primary
families remain selected for every comparison except #132: that PR changes how
the generated decorator constructs its key builder. Its direct string-key memory,
HTTP and L2 benchmarks do not execute the changed constructor or generated call.
Its local contract family and genuine generated prefix family remain selected.
The constructor endpoint still runs all primary families for #133, which changes
hashing in the shared engine. Only the constructor-base endpoint loses the two
unrelated primary families.

The prefix optimization (#128) retains **all** component families: key construction,
lookup, facade calls, separate IMemoryCache references, and genuine generated calls.
The constructor change (#132) retains genuine generated calls on both endpoints.
SipHash (#133) retains lookup and genuine generated calls on both endpoints. Its
facade and key-construction diagnostics duplicate coverage provided by #128 and
the generated path; they are not needed to establish the change's end-to-end cost.
The final cumulative comparison retains all 63 primary cases, including argument
shapes, listeners, misses, pressure, HTTP and both native providers. Extra component
measurements of the final revision do not substitute for that comparison.

This removes 27 of 180 family launches and 792 of 4,482 individual case launches:
**153 family launches / 3,690 cases**, a reduction of **17.67%** in timed cases.
It deliberately makes no unsupported claim that the remaining work takes only a
few hours. Warmups, iterations, affinity, native images, exclusive capacity,
allocation checks and the statistical threshold remain unchanged.

## Order and provenance

Prioritize completing the cumulative final/baseline pairs, alternating endpoint
order across three rounds. Finish the remaining selected PR comparisons afterward,
retaining the descending / ascending / descending order within each comparison.
Already completed V5 launches keep their actual timestamps and original order;
they must never be relabelled as newly executed launches.

Stop the original controller only at a verified idle boundary after a successful
owned batch. Preserve its protocol, inputs, metadata, logs and results. A separate
controller must record its selected schedule and every reused launch before
starting new measurements. It must refuse a live original controller, duplicate
launches, changed frozen inputs, overwritten results or incomplete launch evidence.
Do not stop or change other CI jobs, Redis, SQL Server or shared services.

No timing or allocation result has been used to choose exclusions. Broad confidence
intervals remain insufficient; missing or unselected comparisons are not passes.
Full statistical review remains required after the selected schedule completes.

`selection.json` is the machine-readable family selection, with the exact original
PR merge bases and heads. Generate and check it with `python3 selection.py`.
This file alone does not indicate that the replacement scheduler has been launched.
