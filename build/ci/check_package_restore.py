#!/usr/bin/env python3
"""Exercise strict NuGet restore, including direct-dependency overrides of exact ranges.

The higher Core package is a temporary metadata-only copy of the real package. It is
never built, executed, published or left in the repository's package feed.
"""
import subprocess
import sys
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile

ADDONS = ("MethodCache.Redis", "MethodCache.SqlServer", "MethodCache.Serialization.MessagePack",
          "MethodCache.AspNetCore", "MethodCache.OpenTelemetry", "MethodCache.HttpCaching")


def verify(folder: Path) -> None:
    folder = folder.resolve()
    core = next(folder.glob("MethodCache.[0-9]*.nupkg"))
    with ZipFile(core) as archive:
        nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
        root = ET.fromstring(archive.read(nuspec))
        ns = {"n": root.tag.split("}")[0].strip("{")}
        ET.register_namespace("", ns["n"])
        version = root.find("n:metadata/n:version", ns).text
    with tempfile.TemporaryDirectory(prefix="methodcache-restore-") as directory:
        temporary = Path(directory)
        feed = temporary / "feed"
        feed.mkdir()
        # A distinct major version cannot accidentally satisfy any pre-1.0 exact range.
        mismatch = "99.0.0-contract-test"
        with ZipFile(core) as source, ZipFile(feed / f"MethodCache.{mismatch}.nupkg", "w") as target:
            for entry in source.infolist():
                data = source.read(entry)
                if entry.filename == nuspec:
                    root.find("n:metadata/n:version", ns).text = mismatch
                    data = ET.tostring(root, encoding="utf-8", xml_declaration=True)
                target.writestr(entry, data)

        def restore(name: str, packages: list[tuple[str, str]], reject: bool = False) -> None:
            project = temporary / f"{name}.csproj"
            references = "".join(f'<PackageReference Include="{identifier}" Version="{requested}" />'
                                 for identifier, requested in packages)
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                               '<TargetFramework>net8.0</TargetFramework>'
                               '<WarningsAsErrors>NU1608</WarningsAsErrors>'
                               f'<RestorePackagesPath>{temporary / "packages"}</RestorePackagesPath>'
                               '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>')
            result = subprocess.run(["dotnet", "restore", str(project), "--force", "--source", str(folder),
                                     "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json"],
                                    capture_output=True, text=True)
            output = result.stdout + result.stderr
            if reject:
                if result.returncode == 0 or "error NU1608" not in output:
                    raise RuntimeError(f"{name}: expected strict NU1608 failure\n{output}")
            elif result.returncode:
                raise RuntimeError(f"{name}: matching restore failed\n{output}")
            print(f"PASS {name}", flush=True)

        matching = [("MethodCache", version)] + [(addon, version) for addon in ADDONS]
        restore("matching-core-first", matching)
        restore("matching-core-last", list(reversed(matching)))
        for addon in ADDONS:
            restore(f"mismatched-{addon}", [("MethodCache", mismatch), (addon, version)], reject=True)


if __name__ == "__main__":
    verify(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/packages"))
