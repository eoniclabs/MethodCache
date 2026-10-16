"""BDN discovers generated projects by the benchmark assembly's filename."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


class HarnessProjectNameTests(unittest.TestCase):
    def test_component_project_is_discoverable_by_benchmark_assembly_name(self):
        scripts = Path(__file__).resolve().parent
        root = scripts.parents[2]
        with tempfile.TemporaryDirectory() as directory:
            subprocess.run([sys.executable, str(scripts/'create-prefix-harness.py'),
                            '--runtime-root', str(root), '--workloads-root', str(root),
                            '--output', directory], check=True, capture_output=True, text=True)
            projects = list(Path(directory).glob('*.csproj'))
            self.assertEqual(1, len(projects))
            assembly = ET.parse(projects[0]).findtext('.//AssemblyName') or projects[0].stem
            self.assertEqual(assembly, projects[0].stem,
                             'BDN must discover the project by its benchmark assembly name')


if __name__ == '__main__':
    unittest.main()
