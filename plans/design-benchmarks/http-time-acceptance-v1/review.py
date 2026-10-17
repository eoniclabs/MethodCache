"""Replay retained complete case cohorts with Johan's approved, exact time bounds.

Run from any directory. No timings are collected and no cross-study means are pooled.
The complete original studies keep their failed verdicts. Only whole-case precision
confirmations replace earlier insufficient evidence in the final coverage inventory.
"""
from pathlib import Path
import hashlib
import importlib.util
import json
import math
import statistics
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
EVIDENCE = ROOT / "plans/design-benchmarks"
RULES = ROOT / "benchmarks/MethodCache.CostProfile/accepted.json"
PAIRED = ROOT / "benchmarks/MethodCache.CostProfile/paired.py"
COMPARE = ROOT / "benchmarks/MethodCache.CostProfile/compare.py"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def verify_manifest(directory):
    manifest = read(directory / "manifest.json")
    files = manifest["files"]
    rows = files.items() if isinstance(files, dict) else ((r["path"], r["sha256"]) for r in files)
    count = 0
    for name, digest in rows:
        assert sha(directory / name) == digest, (directory.name, name)
        count += 1
    return {"manifest_sha256": sha(directory / "manifest.json"), "files_verified": count}


def load_pairs(directory, pairs, expected_cases):
    paths = sorted(directory.glob("*.jsonl"))
    assert {p.name for p in paths} == {
        f"{side}-{pair}.jsonl" for side in ("base", "head") for pair in range(1, pairs + 1)
    }
    result = {}
    for path in paths:
        records = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
        assert len(records) == expected_cases
        cases = {r["case"]: r for r in records}
        assert len(cases) == expected_cases
        for record in records:
            assert math.isfinite(record["ns"]) and record["ns"] > 0
            assert len(record["actualMeasurements"]) == record["actualIterations"] == 12
        result[path.name] = cases
    assert all(set(v) == set(next(iter(result.values()))) for v in result.values())
    return result


def run(label, command, expected_exit):
    result = subprocess.run(command, text=True, capture_output=True)
    (HERE / (label + ".log")).write_text(result.stdout + result.stderr)
    (HERE / (label + ".exit")).write_text(str(result.returncode) + "\n")
    assert result.returncode == expected_exit, (label, result.returncode, result.stderr)
    return result.returncode


def timing(label, records, cases, expected_exit=0):
    with tempfile.TemporaryDirectory(prefix="methodcache-accepted-cases-") as temp:
        directory = Path(temp)
        for name, original in records.items():
            assert set(cases) <= original.keys()
            # Every original pair and actual iteration for each selected whole case.
            (directory / name).write_text("\n".join(json.dumps(original[c]) for c in sorted(cases)) + "\n")
        return run(label, [sys.executable, str(PAIRED), str(directory), "--accepted", str(RULES),
                           "--markdown", str(HERE / (label + ".md"))], expected_exit)


def verdict(records, case, rules, paired):
    count = len(records) // 2
    logs = [math.log(records[f"head-{i}.jsonl"][case]["ns"] /
                     records[f"base-{i}.jsonl"][case]["ns"]) for i in range(1, count + 1)]
    mean = statistics.fmean(logs)
    half = paired.t95(count - 1) * statistics.stdev(logs) / math.sqrt(count)
    low, ratio, high = math.exp(mean - half), math.exp(mean), math.exp(mean + half)
    rule = next((r for k, r in rules.items() if not k.startswith("_") and k in case and "time" in r), None)
    assert high - low <= 0.06, (case, "insufficient precision", low, high)
    if low > 1 and ratio > 1.03:
        assert rule and ratio <= rule["time"], (case, "unaccepted slowdown", ratio)
        state = "accepted"
    else:
        state = "faster" if high < 1 and ratio < 0.97 else "same"
    return {"case": case, "pairs": count, "ratio": ratio, "low": low, "high": high,
            "verdict": state, "time_cap": rule["time"] if state == "accepted" else None}


def main():
    decision = read(HERE / "decision.json")
    assert decision["decision"] == "approved" and sha(RULES) == decision["accepted_rules_sha256"]
    before, rules = read(HERE / "rules-before.json"), read(RULES)
    assert sha(HERE / "rules-before.json") == decision["previous_rules_sha256"]
    added = set(rules) - set(before)
    assert added == set(decision["rules"]) and len(added) == 5
    for key in before:
        if not key.startswith("_"):
            assert before[key] == rules[key], key
    for key in added:
        assert set(rules[key]) == {"time", "reason"}
        assert rules[key]["time"] == decision["rules"][key]

    impact = read(HERE / "source-impact.json")
    assert sha(HERE / "source-impact.patch") == impact["patch_sha256"]
    for source in impact["current_files"]:
        assert sha(ROOT / source["path"]) == source["sha256"], source["path"]

    studies = {
        "cpu53": ("mac-final-cpu53-v4", "paired-input", 10, 53),
        "nonhttp9": ("mac-nonhttp-warm32-v2", "paired-input", 20, 9),
        "compact10": ("mac-compact-telemetry-cpu-v1", "paired-input", 10, 10),
        "mac14": ("http-client-index-final14-v1", "raw/paired-input", 10, 14),
        "nimbus2": ("http-precision2-nimbus-v1", "raw/paired-input", 10, 2),
    }
    manifests, data = {}, {}
    for label, (folder, path, count, cases) in studies.items():
        manifests[label] = verify_manifest(EVIDENCE / folder)
        data[label] = load_pairs(EVIDENCE / folder / path, count, cases)

    cases = {label: set(next(iter(records.values()))) for label, records in data.items()}
    classification_path = EVIDENCE / "final-paired-time-v3/l2-gate-classification.json"
    classification = read(classification_path)
    assert cases["cpu53"] == set(classification["existing_local_cpu_cases"])
    assert cases["nimbus2"] == {
        "HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True)",
        "HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True)",
    }
    # Keep the original full Mac failure: these two intervals are still too wide.
    reviews = {"original_mac14_with_approved_rules": timing("original-mac14", data["mac14"], cases["mac14"], 1)}
    reviews["mac12"] = timing("mac12", data["mac14"], cases["mac14"] - cases["nimbus2"])
    reviews["nimbus2"] = timing("nimbus2", data["nimbus2"], cases["nimbus2"])

    # Selection is by complete scenario, source impact and precision follow-up,
    # never by favorable individual pair. Keep the whole newer confirmations too.
    sources = {case: "cpu53" for case in cases["cpu53"]}
    for label in ("nonhttp9", "compact10", "mac14", "nimbus2"):
        sources.update({case: label for case in cases[label]})
    assert len(sources) == 59
    for label in ("cpu53", "nonhttp9", "compact10"):
        selected = {case for case, source in sources.items() if source == label}
        reviews[label] = timing(label, data[label], selected)

    spec = importlib.util.spec_from_file_location("paired_review", PAIRED)
    paired = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(paired)
    coverage = []
    for case, label in sorted(sources.items()):
        item = verdict(data[label], case, rules, paired)
        item["study"] = studies[label][0]
        item["paired_input"] = studies[label][1]
        coverage.append(item)
    assert sum(row["verdict"] == "accepted" for row in coverage) == 5
    assert set(decision["rules"]) == {row["case"] for row in coverage if row["verdict"] == "accepted"}

    cost = EVIDENCE / "http-client-index-cost-v2"
    manifests["cost67"] = verify_manifest(cost)
    cost_paths = [cost / "raw/results" / (side + ".jsonl") for side in ("base", "head")]
    for path in cost_paths:
        rows = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
        assert len(rows) == len({r["case"] for r in rows}) == 67
        assert all("error" not in row for row in rows)
    reviews["cost67"] = run("cost67", [sys.executable, str(COMPARE), *map(str, cost_paths),
                                      "--accepted", str(RULES), "--markdown", str(HERE / "cost67.md")], 0)
    assert "0 failing, 6 accepted, 67 compared" in (HERE / "cost67.log").read_text()
    result = {"scope": "Case-scoped accepted final CPU coverage; all53existinglocalcases plus6affectedprefixmisses. No cross-study averaging; original full failed cohorts remain unchanged.",
              "rules_sha256": sha(RULES), "decision_sha256": sha(HERE / "decision.json"),
              "paired_reviewer_sha256": sha(PAIRED), "cost_reviewer_sha256": sha(COMPARE),
              "original_case_classification_sha256": sha(classification_path),
              "source_impact_sha256": sha(HERE / "source-impact.json"),
              "verified_evidence": manifests, "review_exits": reviews,
              "covered_cases": len(coverage), "accepted_time_cases": 5,
              "missing": 0, "too_wide": 0, "unaccepted_slowdowns": 0,
              "costs": "67 compared, 0 failing, 6 unchanged accepted costs", "cases": coverage}
    (HERE / "coverage.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({k: v for k, v in result.items() if k not in ("cases", "verified_evidence")}, indent=2))


if __name__ == "__main__":
    main()
