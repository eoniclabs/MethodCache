#!/usr/bin/env python3
"""Verify that every integration's packed Core dependency is an exact matching version."""
import sys
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile

def verify(folder: Path) -> None:
    packages = {}
    for path in folder.glob("*.nupkg"):
        with ZipFile(path) as archive:
            root = ET.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith(".nuspec"))))
        ns = {"n": root.tag.split("}")[0].strip("{")}
        metadata = root.find("n:metadata", ns)
        package_id = metadata.findtext("n:id", namespaces=ns)
        version = metadata.findtext("n:version", namespaces=ns)
        packages[package_id] = (version, metadata, ns)
    core_version = packages["MethodCache"][0]
    for package_id in ("MethodCache.Redis", "MethodCache.SqlServer", "MethodCache.Serialization.MessagePack",
                       "MethodCache.AspNetCore", "MethodCache.OpenTelemetry", "MethodCache.HttpCaching"):
        version, metadata, ns = packages[package_id]
        assert version == core_version, f"{package_id}: {version} differs from Core {core_version}"
        dependencies = metadata.findall(".//n:dependency[@id='MethodCache']", ns)
        assert dependencies, f"{package_id} has no Core dependency"
        for dependency in dependencies:
            assert dependency.attrib["version"] == f"[{core_version}]", f"{package_id}: Core dependency is not exact"
    print(f"All six integration packages require MethodCache [{core_version}].")

if __name__ == "__main__":
    verify(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/packages"))
