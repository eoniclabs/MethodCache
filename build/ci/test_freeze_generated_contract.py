"""Immutable contract images must never be replaced by the explicit freezer."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('freeze_contract', Path(__file__).with_name('freeze_generated_contract.py'))
freezer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(freezer)


class FreezeContractTests(unittest.TestCase):
    def test_existing_baseline_is_refused_before_any_build_or_git_command(self):
        path = Path(__file__).resolve().parents[2]/'tests/MethodCache.CompatTests/Baselines/Contract1/manifest.json'
        original = path.read_bytes()
        with patch.object(freezer.subprocess, 'check_output') as query, patch.object(freezer.subprocess, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'must never be overwritten'):
                freezer.freeze('0'*40)
        query.assert_not_called()
        run.assert_not_called()
        self.assertEqual(original, path.read_bytes())

    def test_baseline_directory_cannot_escape_the_fixture_root(self):
        for name in ('../Contract1', '/tmp/vendor', 'Contract1/child'):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'not a path'):
                freezer.freeze('0'*40, name)


if __name__ == '__main__': unittest.main()
