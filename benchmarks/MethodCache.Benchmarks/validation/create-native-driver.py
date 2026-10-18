"""Create the disposable native benchmark driver outside the repository."""
from pathlib import Path
import argparse
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source = Path(__file__).resolve().parent
args.output.mkdir(parents=True, exist_ok=True)
shutil.copyfile(source / 'NativeDriver.cs.txt', args.output / 'Program.cs')
shutil.copyfile(source / 'NativeDriver.csproj.txt', args.output / 'Runner.csproj')
print(args.output / 'Runner.csproj')
