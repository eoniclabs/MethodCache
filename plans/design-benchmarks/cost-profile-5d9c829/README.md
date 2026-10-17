# PR137 merge-head cost profile against f43854d

Runtime `5d9c829a857bd377049b3bd18703e97cdb254547`, baseline `f43854dbcd3646893f490e78a0a1d9651e9c4521`. The unchanged §10 cost-profile script runs every one of 67 cases on both sides with real isolated Redis and SQL Server: **zero failures, six accepted costs** under the unchanged `accepted.json`. No execution errors or missing/duplicate cases.

Redis HTTP Fresh: ten read-only commands (three EVALSHA, four HMGET, three TTL), 14440.1 B/op below the accepted 14800 bound. SQL HTTP Fresh: two commands, 24009.9 B/op below 24500. Method L2 Read (RoundTrip=true): Redis 5360.4→5216 B/op; SQL 10072.7→9856.1 B/op. Full results and every accepted reason are in [report.md](results/report.md). These do not establish timing acceptance.

The exclusive bounded lease ran 20:43:53–20:46:32 UTC on 2026-10-16, including builds. Both sides use the same pinned SDK image and workstation concurrent GC; pinned backend images, CPU/affinity, full SHAs and command are in the receipt and SDK output. Only newly created exact-label containers were used; cleanup removed them and released the lease. Other jobs/services were untouched.

The manifest preserves original/stored SHA256 values for all sixteen archived source/result/log files and all compiled binary checksums. Only root paths/line endings/trailing whitespace and catalogue representation are normalized; full original archive/manifest checksums are retained, binaries omitted. The original archive is retained outside the repository and on the measurement host. Subsequent timing-tool-only commits change no shipped runtime source.
