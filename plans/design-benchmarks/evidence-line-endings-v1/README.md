# Exact evidence bytes survive Git normalization

The Windows checkout console is retained with CRLF. Git initially normalizes its
blob to LF under `text=auto`, so checking only working-file hashes misses a real
manifest mismatch. The regression fixture reproduces this with `core.autocrlf=true`:
all six format subtests fail before the fix. After evidence paths disable text
conversion, all five repository-path/byte-preservation tests pass. Logs, JSONL,
JSON, Markdown, patches and PowerShell retain their exact indexed bytes.

The actual console bytes are restored in Git. The independent
[Git-object verifier](verify-git-blobs.py) checks all 7,521 files and metadata in
all 27 published proof directories against staged/committed objects, including
manifest and README bytes. It passes with zero mismatches. The verifier does not
claim timing or final CI acceptance; it establishes evidence transport integrity.

Run `python3 plans/design-benchmarks/evidence-line-endings-v1/verify-git-blobs.py`
and `python3 -m unittest discover -s build/ci/tests -p test_repository_paths.py`.
Ordinary source EOL rules are unchanged. No measurement or acceptance bound changes.
