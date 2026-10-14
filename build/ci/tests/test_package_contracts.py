import importlib.util
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location("package_contracts", Path(__file__).resolve().parents[1] / "check_package_contracts.py")
contracts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contracts)

class PackageContractTests(unittest.TestCase):
    def write_packages(self, root, bound="[1.2.3]"):
        names = ["MethodCache", "MethodCache.Redis", "MethodCache.SqlServer", "MethodCache.Serialization.MessagePack",
                 "MethodCache.AspNetCore", "MethodCache.OpenTelemetry", "MethodCache.HttpCaching"]
        for name in names:
            dependencies = "" if name == "MethodCache" else f'<dependencies><group targetFramework="net8.0"><dependency id="MethodCache" version="{bound}"/></group></dependencies>'
            with ZipFile(root / f"{name}.1.2.3.nupkg", "w") as archive:
                archive.writestr(name + ".nuspec", f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{name}</id><version>1.2.3</version>{dependencies}</metadata></package>')

    def test_matching_exact_bounds_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.write_packages(root)
            contracts.verify(root)

    def test_minimum_only_dependency_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.write_packages(root, "1.2.3")
            with self.assertRaises(AssertionError):
                contracts.verify(root)

    def test_different_exact_dependency_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.write_packages(root, "[1.2.4]")
            with self.assertRaises(AssertionError):
                contracts.verify(root)
