import unittest
from bdn_affinity import exported_affinity_mask, owned_dotnet_affinities
from process_activity import Process


class AffinityEvidenceTests(unittest.TestCase):
    def test_exported_short_binary_masks_match_actual_bdn_display(self):
        display = 'GeneratedContractBenchmarks.SyncHit: Job-IRUZYG(Affinity=100, Concurrent=True, Server=False, IterationCount=12)'
        self.assertEqual(4, exported_affinity_mask(display))
        self.assertEqual(2, exported_affinity_mask('Job(Affinity=10)'))
        self.assertEqual(3, exported_affinity_mask('Job(Affinity=11)'))

    def test_exported_padding_does_not_change_the_mask(self):
        self.assertEqual(4, exported_affinity_mask('Job(Affinity=0000000000000100, Concurrent=True)'))
        self.assertEqual(1, exported_affinity_mask('Affinity=1'))
        self.assertEqual(0, exported_affinity_mask('Affinity=0'))

    def test_exported_missing_invalid_or_duplicate_masks_are_rejected(self):
        for display in ['Job(Default)', 'Affinity=4', 'Affinity=0x4', 'Affinity=12',
                        'Affinity=100x', 'Affinity=100.0', 'Affinity=100, Affinity=100']:
            with self.subTest(display=display), self.assertRaises(ValueError):
                exported_affinity_mask(display)

    def test_os_observation_covers_owned_dotnet_descendants_only(self):
        rows = [Process(10, 1, 'dotnet root.dll'), Process(11, 10, 'bash compiler'),
                Process(12, 11, '/usr/share/dotnet/dotnet child.dll'), Process(20, 1, 'dotnet external.dll')]
        seen = []
        def read(pid):
            seen.append(pid)
            return {2}
        self.assertEqual({10: [2], 12: [2]}, owned_dotnet_affinities(rows, 10, read))
        self.assertEqual([10, 12], seen)

    def test_os_observation_ignores_a_process_that_exited_after_snapshot(self):
        rows = [Process(10, 1, 'dotnet root.dll'), Process(12, 10, 'dotnet child.dll')]
        def read(pid):
            if pid == 12:
                raise ProcessLookupError()
            return {2}
        self.assertEqual({10: [2]}, owned_dotnet_affinities(rows, 10, read))

    def test_os_observation_preserves_an_unexpected_mask_for_rejection(self):
        rows = [Process(10, 1, 'dotnet root.dll'), Process(12, 10, 'dotnet child.dll')]
        self.assertEqual({10: [2], 12: [3]}, owned_dotnet_affinities(rows, 10, lambda pid: {2} if pid == 10 else {3}))
