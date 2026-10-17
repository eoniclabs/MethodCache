#!/usr/bin/env python3
"""Explicitly freeze a vendor baseline; never called by ordinary builds or CI jobs."""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile


def freeze(source_commit: str, baseline_name: str = "Contract1", framework: str = "net10.0") -> None:
    root = Path(__file__).resolve().parents[2]
    if not re.fullmatch(r"[A-Za-z0-9_-]+", baseline_name):
        raise ValueError("Use a baseline directory name, not a path.")
    baseline = root / "tests/MethodCache.CompatTests/Baselines" / baseline_name
    if (baseline / "manifest.json").exists():
        raise RuntimeError("Frozen baselines must never be overwritten; create a new baseline directory.")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", source_commit) or source_commit != head:
        raise RuntimeError("Pass the verified full HEAD commit, not a branch name or abbreviated SHA.")
    subprocess.run(["git", "diff", "--exit-code", "HEAD", "--", "src", str(baseline / "source"),
                    str(Path(__file__).resolve())], cwd=root, check=True)
    with tempfile.TemporaryDirectory(prefix="methodcache-freeze-") as temporary:
        subprocess.run(["dotnet", "pack", "src/MethodCache.Core", "-c", "Release", "-p:CI=true", "-o", temporary],
                       cwd=root, check=True)
        package = next(Path(temporary).glob("MethodCache.[0-9]*.nupkg"))
        with ZipFile(package) as archive:
            metadata = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith(".nuspec"))))
            ns = {"n": metadata.tag.split("}")[0].strip("{")}
            version = metadata.findtext("n:metadata/n:version", namespaces=ns)
            generator = archive.read("analyzers/dotnet/cs/MethodCache.SourceGenerator.dll")
            core = archive.read(f"lib/{framework}/MethodCache.Core.dll")
        images = []
        for project in ("ThirdParty", "Vendor", "Incompatible"):
            source = baseline / "source" / project
            project_file = source / f"{project}.csproj"
            project_xml = ET.parse(project_file).getroot()
            if project_xml.findtext(".//TargetFramework") != framework:
                raise ValueError(f"{project_file} must explicitly target {framework}.")
            image = project_xml.findtext(".//AssemblyName")
            if not image or not re.fullmatch(r"[A-Za-z0-9_.-]+", image):
                raise ValueError(f"{project_file} must have an explicit assembly name.")
            subprocess.run(["dotnet", "build", str(project_file), "-c", "Release", "-p:CI=true"],
                           cwd=root, check=True)
            data = (source / f"bin/Release/{framework}" / f"{image}.dll").read_bytes()
            (baseline / f"{image}.dll").write_bytes(data)
            images.append({"file": f"{image}.dll", "sha256": hashlib.sha256(data).hexdigest()})
        sources = {str(path.relative_to(baseline)): hashlib.sha256(path.read_bytes()).hexdigest()
                   for path in sorted((baseline / "source").rglob("*"))
                   if path.is_file() and path.suffix in (".cs", ".csproj") and not {"bin", "obj"}.intersection(path.parts)}
        manifest = {"contract": 1, "source_commit": source_commit, "package_version": version,
                    "target_framework": framework, "core_sha256": hashlib.sha256(core).hexdigest(),
                    "generator_sha256": hashlib.sha256(generator).hexdigest(), "sources": sources, "images": images}
        (baseline / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Frozen contract 1 from {source_commit}, package {version}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--baseline-directory", default="Contract1")
    parser.add_argument("--target-framework", choices=["net10.0"], default="net10.0")
    args = parser.parse_args()
    freeze(args.source_commit, args.baseline_directory, args.target_framework)
