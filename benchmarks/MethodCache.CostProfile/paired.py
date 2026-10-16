#!/usr/bin/env python3
"""Compares alternating timing launches of two revisions, pair by pair.

Each pair is one base launch and one head launch run back to back, so slow phases of the machine affect both.
The head/base ratio of each pair is averaged in log space; the 95 % interval uses Student's t with n-1 degrees
of freedom. A case is slower when the whole interval lies above 1 and the estimate is more than 3 % above it.

usage: paired.py <directory with base-<i>.jsonl and head-<i>.jsonl> [--markdown report.md]
"""
import argparse
import glob
import json
import math
import os
import statistics
import sys

T95 = {1: 12.706, 2: 4.303, 3: 3.182, 4: 2.776, 5: 2.571, 6: 2.447, 7: 2.365, 8: 2.306, 9: 2.262, 10: 2.228,
       11: 2.201, 12: 2.179, 13: 2.160, 14: 2.145, 15: 2.131, 19: 2.093, 24: 2.064, 29: 2.045}


def t95(df):
    return T95.get(df) or T95[max(k for k in T95 if k <= df)]


def load(path):
    with open(path, encoding="utf-8") as lines:
        return {r["case"]: r["ns"] for r in map(json.loads, filter(str.strip, lines)) if r.get("ns")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    parser.add_argument("--markdown")
    args = parser.parse_args()
    pairs = []
    for base_path in sorted(glob.glob(os.path.join(args.directory, "base-*.jsonl"))):
        head_path = base_path.replace("base-", "head-")
        if os.path.exists(head_path):
            pairs.append((load(base_path), load(head_path)))
    if len(pairs) < 2:
        sys.exit("need at least two pairs")
    lines = ["| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |", "|---|---:|---:|---:|---|---|"]
    slower = 0
    for case in sorted(set.intersection(*(set(b) & set(h) for b, h in pairs))):
        logs = [math.log(h[case] / b[case]) for b, h in pairs]
        mean = statistics.fmean(logs)
        half = t95(len(logs) - 1) * statistics.stdev(logs) / math.sqrt(len(logs))
        low, estimate, high = math.exp(mean - half), math.exp(mean), math.exp(mean + half)
        if low > 1 and estimate > 1.03:
            verdict, slower = "**slower**", slower + 1
        elif high < 1 and estimate < 0.97:
            verdict = "faster"
        elif high - low > 0.06:
            verdict = "too noisy"
        else:
            verdict = "same"
        base_ns = statistics.median(b[case] for b, _ in pairs)
        head_ns = statistics.median(h[case] for _, h in pairs)
        lines.append(f"| {case} | {base_ns:.1f} | {head_ns:.1f} | {estimate:.3f} | {low:.3f}-{high:.3f} | {verdict} |")
    report = "\n".join(lines)
    print(report)
    print(f"\n{len(pairs)} pairs, {slower} slower")
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as file:
            file.write(report + "\n")
    return 1 if slower else 0


if __name__ == "__main__":
    sys.exit(main())
