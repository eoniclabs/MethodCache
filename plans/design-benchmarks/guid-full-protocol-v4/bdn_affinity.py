"""Validate declared BDN affinity separately from observed OS affinity."""
import re
from pathlib import Path


def exported_affinity_mask(display):
    """Read the declared binary mask, not evidence of the process's actual OS mask.

    BDN 0.15.4 uses Convert.ToString(mask, 2).PadLeft(Environment.ProcessorCount, '0').
    https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Extensions/ProcessExtensions.cs
    """
    values = re.findall(r'(?<![A-Za-z0-9_])Affinity=([^\s,)]+)', display)
    if len(values) != 1 or re.fullmatch('[01]+', values[0]) is None:
        raise ValueError('Expected exactly one BDN binary affinity display')
    return int(values[0], 2)


def owned_dotnet_affinities(processes, owned_pid, read_affinity):
    """Observe only the owned .NET process tree; never record arguments or change affinity."""
    owned = {owned_pid}
    while True:
        children = {p.pid for p in processes if p.parent in owned}
        if children.issubset(owned):
            break
        owned.update(children)
    observed = {}
    for process in processes:
        if process.pid not in owned or Path(process.command.split()[0]).name != 'dotnet':
            continue
        try:
            observed[process.pid] = sorted(read_affinity(process.pid))
        except ProcessLookupError:
            pass  # A short-lived build descendant can exit between the snapshot and the OS query.
    return observed
