#!/usr/bin/env python3
"""Writes llms.txt and llms-full.txt from the user docs, in the order docs/README.md lists them.

CI runs this and fails when the committed files differ, so they never drift from the docs.
Usage: python3 .github/scripts/generate-llms.py
"""
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BLOB = "https://github.com/eoniclabs/MethodCache/blob/main/"
RAW = "https://raw.githubusercontent.com/eoniclabs/MethodCache/main/"

LINK = re.compile(r"\]\(([^)\s]+)\)")
SNIPPET_MARKER = re.compile(r"^<!-- (snippet: [\w-]+|endSnippet) -->\n", re.M)


def read(path):
    with open(os.path.join(ROOT, path), encoding="utf-8") as f:
        return f.read()


def pages():
    """The pages docs/README.md links to, in order, with the description it gives them (after the link and a colon)."""
    found = {}
    for line in read("docs/README.md").splitlines():
        if not line.lstrip().startswith(("- [", "[")):
            continue
        for m in re.finditer(r"\[([^\]]+)\]\(([^)\s]+)\)(?::\s*(.+))?", line):
            link = m.group(2)
            if link.startswith(("http", "#")):
                continue
            path = os.path.normpath(os.path.join("docs", link.split("#")[0])).replace(os.sep, "/")
            if path.endswith(".md") and path != "docs/benchmarks.md" and path not in found:
                found[path] = strip_links(m.group(3)) if m.group(3) and line.lstrip().startswith("- [") else ""
    return found


def strip_links(text):
    return re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text).strip()


def title_and_summary(path):
    text = SNIPPET_MARKER.sub("", read(path))
    title = re.search(r"^# (.+)$", text, re.M).group(1).strip()
    summary = ""
    for block in re.split(r"\n\s*\n", text.split("\n", 1)[1]):
        block = block.strip()
        if block and not block.startswith(("#", "```", "<", "|", "[!", "-", "snippet:")):
            summary = " ".join(block.split())
            summary = strip_links(re.split(r"(?<=[.!?])\s", summary)[0]).rstrip(":")
            break
    return title, summary


def absolute_links(text, path):
    def repl(m):
        link = m.group(1)
        if link.startswith(("http", "#", "mailto:")):
            return m.group(0)
        target, _, anchor = link.partition("#")
        resolved = os.path.normpath(os.path.join(os.path.dirname(path), target)).replace(os.sep, "/")
        return "](" + BLOB + resolved + ("#" + anchor if anchor else "") + ")"
    return LINK.sub(repl, text)


def main():
    readme = read("README.md")
    tagline = re.search(r"^\*\*(.+?)\*\*", readme, re.M).group(1)
    docs = pages()

    index = [
        "# MethodCache",
        "",
        "> " + tagline + " A .NET 10 library: a source generator turns `[Cache]` on interface "
        "methods into caching decorators at compile time, with keys built from the arguments, tag invalidation, "
        "a memory cache plus optional Redis or SQL Server, and policies you can change in appsettings.json or at runtime.",
        "",
        "Put `[Cache]` and `[CacheInvalidate]` on interface methods (not classes), register services, then call "
        "`builder.Services.AddMethodCache()` after them. Every C# example in these docs is compiled in CI. "
        "For coding agents, " + "[docs/ai/methodcache.md](" + RAW + "docs/ai/methodcache.md) is a compact rule set "
        "and [llms-full.txt](" + RAW + "llms-full.txt) has all the docs in one file.",
        "",
        "## Docs",
        "",
    ]
    for path, description in docs.items():
        title, summary = title_and_summary(path)
        summary = description or summary
        index.append(f"- [{title}]({RAW}{path})" + (f": {summary}" if summary else ""))
    index += ["", "## Optional", "", f"- [README]({RAW}README.md): overview, benchmarks and comparison with HybridCache and FusionCache",
              f"- [Benchmarks]({RAW}docs/benchmarks.md)", ""]

    full = ["# MethodCache documentation", "",
            "All user documentation in one file, generated from the repository. Source: " + BLOB + "docs/README.md", ""]
    for path in ["docs/ai/methodcache.md"] + [p for p in docs if p != "docs/ai/methodcache.md"]:
        text = SNIPPET_MARKER.sub("", read(path)).strip()
        full += ["", "---", "", f"<!-- source: {path} -->", "", absolute_links(text, path), ""]

    with open(os.path.join(ROOT, "llms.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(index))
    with open(os.path.join(ROOT, "llms-full.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(full).rstrip() + "\n")


if __name__ == "__main__":
    main()
