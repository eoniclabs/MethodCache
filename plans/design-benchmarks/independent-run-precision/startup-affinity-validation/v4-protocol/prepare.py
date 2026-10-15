from pathlib import Path
import subprocess
folder=Path(__file__).resolve().parent
probe=folder/'startup-probe';probe.mkdir(exist_ok=True)
(probe/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
(probe/'Program.cs').write_text('using System.Diagnostics; if (!OperatingSystem.IsLinux()) throw new Exception("Linux probe required"); var count = Environment.ProcessorCount; var affinity = Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); Console.WriteLine($"ProcessorCount={count};Affinity={affinity}"); if (count != 1 || affinity != 4) throw new Exception("Startup CPU availability mismatch");')
subprocess.run(['dotnet','build',str(probe/'Probe.csproj'),'-c','Release','--nologo','-v','minimal'],check=True)
text=subprocess.check_output(['taskset','--cpu-list','2','dotnet',str(probe/'bin/Release/net10.0/Probe.dll')],text=True)
assert text.strip()=='ProcessorCount=1;Affinity=4'
(folder/'startup-probe.txt').write_text(text);print(text,flush=True)
