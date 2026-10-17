# Windows checkout regression: portable proof paths

Published f7520375 fails Windows checkout because AUX is a reserved device name,
even when used as a directory. The retained console snapshot shows the actual
invalid-path errors. No library build or test failed in that Windows checkout;
the platform never reached compilation.

The new repository-path guard fails first on the tracked evidence directory.
After renaming `final-paired-time-v3/aux` to `additional`, all four checks pass:
tracked paths, reserved names with extensions, ordinary names and invalid
characters/suffixes. The existing Linux build job discovers this test automatically.

All 375 moved files retain their exact hashes. The manifest records the mapping,
and the updated metadata restorer verifies all 3,168 original archive file hashes.
Original labels inside retained JSON stay intact. This fix changes no runtime,
measurement, allocation limit or timing verdict. New-head three-platform CI is
still required; running jobs and shared services are untouched.

Run `python3 -m unittest discover -s build/ci/tests -p test_repository_paths.py`.
