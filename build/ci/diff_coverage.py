#!/usr/bin/env python3
"""Line coverage of the lines a change adds or edits in src/, from Cobertura reports.

    python3 build/ci/diff_coverage.py [--base <commit>] [--min 80] <coverage.cobertura.xml>...

Compares the working tree with the merge base of HEAD and origin/main (or --base), keeps the added and changed
lines of C# files under src/, and looks them up in the reports (a line counts as covered when any report ran it).
Lines no report knows (blank lines, braces, declarations) are not executable and do not count; neither do files
that no report measured at all, which are listed. Prints every uncovered changed line and exits 1 when the share
of covered changed lines is below --min percent.

Paths in the reports may be absolute, relative to a <source> or deterministic (/_/src/...), as a CI build
writes them; all three are mapped back to the repository.
"""

import argparse
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ElementTree


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def default_base():
    for ref in ("origin/main", "main"):
        try:
            return git("merge-base", "HEAD", ref).strip()
        except subprocess.CalledProcessError:
            continue
    sys.exit("diff-coverage: no origin/main or main to compare with; pass --base")


def changed_lines(base):
    """{repo path: set of line numbers} for the added or changed lines of C# files under src/."""
    diff = git("diff", "--no-color", "--no-ext-diff", "--unified=0", "--diff-filter=AMR", base, "--", "src")
    changes = {}
    path = None
    for line in diff.splitlines():
        if line.startswith("+++ "):
            name = line[4:]
            path = name[2:] if name.startswith("b/") else None
            if path and (not path.endswith(".cs") or "/obj/" in path or path.endswith(".g.cs")):
                path = None
        elif path and line.startswith("@@"):
            match = re.match(r"@@ -\S+ \+(\d+)(?:,(\d+))? @@", line)
            start, count = int(match.group(1)), int(match.group(2) or "1")
            changes.setdefault(path, set()).update(range(start, start + count))
    return {path: lines for path, lines in changes.items() if lines}


def coverage(reports, root, wanted):
    """{repo path: {line: hits}} from the reports, for the files in `wanted`."""
    def to_repo_path(name, sources):
        name = name.replace("\\", "/")
        candidates = [name] if name.startswith("/") or re.match(r"^[A-Za-z]:/", name) else \
            [os.path.join(source.replace("\\", "/"), name) for source in sources] + [name]
        for candidate in candidates:
            candidate = os.path.normpath(candidate).replace("\\", "/")
            if candidate.startswith(root + "/"):
                candidate = candidate[len(root) + 1:]
            elif candidate.startswith("/_/"):
                candidate = candidate[3:]
            if candidate in wanted:
                return candidate
            # Any other prefix (another checkout, a container path): match on the end of the path
            for path in wanted:
                if candidate.endswith("/" + path):
                    return path
        return None

    hits = {}
    for report in reports:
        tree = ElementTree.parse(report)
        sources = [source.text or "" for source in tree.iter("source")]
        for cls in tree.iter("class"):
            path = to_repo_path(cls.get("filename", ""), sources)
            if path is None:
                continue
            file_hits = hits.setdefault(path, {})
            for line in cls.iter("line"):
                number = int(line.get("number"))
                file_hits[number] = max(file_hits.get(number, 0), int(line.get("hits", "0")))
    return hits


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", help="commit to compare with (default: merge base of HEAD and origin/main)")
    parser.add_argument("--min", type=float, default=80.0, help="minimum percent of changed lines covered")
    parser.add_argument("reports", nargs="+", help="Cobertura XML reports")
    args = parser.parse_args()

    root = git("rev-parse", "--show-toplevel").strip()
    os.chdir(root)
    base = git("rev-parse", "--verify", (args.base or default_base()) + "^{commit}").strip()
    changes = changed_lines(base)
    print(f"Changed C# lines under src/ since {base[:12]}: {sum(map(len, changes.values()))} in {len(changes)} file(s)")
    if not changes:
        print("Nothing to measure.")
        return 0

    hits = coverage(args.reports, root, set(changes))
    total = covered = 0
    unmeasured = []
    for path in sorted(changes):
        if path not in hits:
            unmeasured.append(path)
            continue
        executable = sorted(line for line in changes[path] if line in hits[path])
        missed = [line for line in executable if hits[path][line] == 0]
        total += len(executable)
        covered += len(executable) - len(missed)
        if not executable:
            continue
        print(f"  {path}: {len(executable) - len(missed)}/{len(executable)} changed lines covered")
        if missed:
            with open(path, encoding="utf-8", errors="replace") as source:
                text = source.read().splitlines()
            for line in missed:
                print(f"      {line:>5} | {text[line - 1].strip() if line <= len(text) else ''}")

    for path in unmeasured:
        print(f"  {path}: not measured (no report covers this file)")

    if total == 0:
        print("No executable changed lines.")
        return 0
    percent = 100.0 * covered / total
    print(f"Diff coverage: {covered}/{total} changed lines = {percent:.1f}% (minimum {args.min:g}%)")
    if percent < args.min:
        print("Below the minimum: add tests for the uncovered lines above.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
