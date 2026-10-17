"""Keep every tracked path check-outable by the Windows CI worker."""
from pathlib import Path
import re
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[3]
RESERVED = re.compile(r"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", re.IGNORECASE)
INVALID = re.compile(r'[<>:"\\|?*\x00-\x1f]')


def windows_path_problem(path):
    for part in path.split("/"):
        if RESERVED.match(part):
            return "reserved Windows device name"
        if part.endswith((" ", ".")):
            return "trailing space or period"
        if INVALID.search(part):
            return "invalid Windows filename character"
    return None


class RepositoryPathTests(unittest.TestCase):
    def test_raw_measurement_output_is_ignored_and_untracked(self):
        tracked = subprocess.check_output(
            ["git", "ls-files", "--", "plans/design-benchmarks"], cwd=ROOT, text=True
        ).splitlines()
        self.assertEqual([], tracked, "Raw measurement evidence belongs in external artifacts, not Git")
        for path in ["plans/design-benchmarks/run/raw.jsonl", "artifacts/cost-profile/head.jsonl"]:
            with self.subTest(path=path):
                result = subprocess.run(
                    ["git", "check-ignore", "--no-index", "--quiet", "--", path], cwd=ROOT
                )
                self.assertEqual(0, result.returncode, "Measurement outputs must stay ignored")

    def test_tracked_paths_are_valid_on_windows(self):
        names = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
        problems = [(name, windows_path_problem(name)) for name in names if name and windows_path_problem(name)]
        self.assertEqual([], problems, "Tracked paths must allow the Windows worker to clone the repository")

    def test_reserved_devices_with_extensions_are_rejected(self):
        for path in ["aux/data.json", "reports/AUX.txt", "con", "prn.log", "COM1.json", "Lpt9/file", "COM¹.txt"]:
            with self.subTest(path=path):
                self.assertIsNotNone(windows_path_problem(path))

    def test_non_device_names_and_ordinary_extensions_are_allowed(self):
        for path in ["additional/data.json", "auxiliary/file.json", "file.aux", "COM10.txt", "console.cs"]:
            with self.subTest(path=path):
                self.assertIsNone(windows_path_problem(path))

    def test_invalid_characters_and_suffixes_are_rejected(self):
        for path in ["reports/data:1.json", "file.", "file ", "name?.txt", "name\\file", "bad\x01.txt"]:
            with self.subTest(path=path):
                self.assertIsNotNone(windows_path_problem(path))


if __name__ == "__main__":
    unittest.main()
