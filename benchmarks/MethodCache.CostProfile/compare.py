#!/usr/bin/env python3
"""Compares two cost profiles and applies the gate.

The gate fails when, compared with the base revision, a case
  - sends more Redis or SQL commands per operation,
  - writes to Redis on a hit, or
  - allocates more bytes per operation on a hit (more than 8 bytes and 0.5 %),
unless accepted.json accepts that case with a reason and an upper bound the new value stays within. A case that
failed to run on either side fails, so does a case the base measured that the head did not run, and so does a
comparison of no cases.

usage: compare.py base.jsonl head.jsonl [--accepted accepted.json] [--markdown report.md]
"""
import argparse
import json
import re
import sys

REDIS_WRITES = {
    "set", "setex", "psetex", "hset", "hmset", "hsetnx", "hdel", "del", "unlink", "persist", "expire", "pexpire",
    "expireat", "pexpireat", "incr", "incrby", "hincrby", "sadd", "srem", "zadd", "zrem", "lpush", "rpush",
}


def load(path):
    cases = {}
    with open(path, encoding="utf-8") as lines:
        for line in lines:
            if line.strip():
                record = json.loads(line)
                cases[record["case"]] = record
    return cases


def is_hit(case):
    method = re.match(r"[^.]+\.([^(]+)", case).group(1)
    return method.endswith("Hit") or method.endswith("Hits") or method in ("Fresh", "Read")


def redis_total(record):
    return sum(record.get("redis", {}).values())


def redis_writes(record):
    return sum(v for k, v in record.get("redis", {}).items() if k in REDIS_WRITES)


def accepted_for(accepted, case):
    return next((rule for pattern, rule in accepted.items() if pattern in case), None)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("base")
    parser.add_argument("head")
    parser.add_argument("--accepted")
    parser.add_argument("--markdown")
    args = parser.parse_args()
    base, head = load(args.base), load(args.head)
    accepted = {}
    if args.accepted:
        with open(args.accepted, encoding="utf-8") as file:
            accepted = {k: v for k, v in json.load(file).items() if not k.startswith("_")}

    rows, failures, notes = [], [], []
    for case in head:
        h = head[case]
        b = base.get(case)
        if "error" in h:
            failures.append(f"{case}: failed to run: {h['error']}")
            continue
        if b is None:
            notes.append(f"{case}: new case, no base value")
            continue
        if "error" in b:
            failures.append(f"{case}: failed to run on the base: {b['error']}")
            continue
        problems = []
        if redis_total(h) > redis_total(b) + 0.05:
            problems.append(("redis", redis_total(h), f"Redis commands {redis_total(b):.2f} -> {redis_total(h):.2f}"))
        if is_hit(case) and redis_writes(h) > 0.005:
            problems.append(("redisWrites", redis_writes(h), f"Redis writes on a hit: {redis_writes(h):.2f}"))
        if h.get("sqlCommands", 0) > b.get("sqlCommands", 0) + 0.05:
            problems.append(("sql", h["sqlCommands"], f"SQL commands {b.get('sqlCommands', 0)} -> {h['sqlCommands']}"))
        if is_hit(case) and h["bytes"] > b["bytes"] + max(8, b["bytes"] * 0.005):
            problems.append(("bytes", h["bytes"], f"hit bytes {b['bytes']} -> {h['bytes']}"))
        rule = accepted_for(accepted, case)
        verdict = "ok"
        for metric, value, text in problems:
            limit = rule.get(metric) if rule else None
            if limit is not None and value <= limit:
                notes.append(f"{case}: {text} (accepted: {rule['reason']})")
                verdict = "accepted"
            else:
                failures.append(f"{case}: {text}")
                verdict = "FAIL"
                break
        rows.append((case, b, h, verdict))
    for case in base:
        if case not in head:
            failures.append(f"{case}: measured on the base, missing on the head")
    if not rows:
        failures.append("no case was compared")

    lines = ["| Case | Bytes base | Bytes head | Redis base | Redis head | SQL base | SQL head | Gate |", "|---|---:|---:|---|---|---:|---:|---|"]
    for case, b, h, verdict in rows:
        redis = lambda r: ", ".join(f"{k} {v}" for k, v in r.get("redis", {}).items()) or "-"
        sql = lambda r: f"{r['sqlCommands']} ({r['sqlTextChars']:.0f} ch)" if "sqlCommands" in r else "-"
        lines.append(f"| {case} | {b['bytes']} | {h['bytes']} | {redis(b)} | {redis(h)} | {sql(b)} | {sql(h)} | {verdict} |")
    report = "\n".join(lines + [""] + [f"- {n}" for n in notes] + [""] + [f"- **{f}**" for f in failures])
    print(report)
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as file:
            file.write(report + "\n")
    print(f"\n{len(failures)} failing, {sum(1 for r in rows if r[3] == 'accepted')} accepted, {len(rows)} compared")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
