"""Keep every tracked path check-outable by the Windows CI worker."""
from pathlib import Path
import re
import subprocess
import tempfile
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
    def test_hashed_evidence_keeps_exact_bytes_with_windows_git_settings(self):
        payload = b"retained first line\r\nretained second line\r\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet", directory], check=True, capture_output=True)
            subprocess.run(["git", "-C", directory, "config", "core.autocrlf", "true"], check=True)
            subprocess.run(["git", "-C", directory, "config", "core.safecrlf", "false"], check=True)
            (root / ".gitattributes").write_bytes((ROOT / ".gitattributes").read_bytes())
            for extension in ["log", "jsonl", "json", "md", "patch", "ps1"]:
                with self.subTest(extension=extension):
                    relative = f"plans/design-benchmarks/example/raw.{extension}"
                    path = root / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(payload)
                    subprocess.run(["git", "-C", directory, "add", "-f", "--", relative], check=True, capture_output=True)
                    stored = subprocess.check_output(["git", "-C", directory, "show", f":{relative}"])
                    self.assertEqual(payload, stored, "Git must preserve the exact bytes indexed by evidence manifests")

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
