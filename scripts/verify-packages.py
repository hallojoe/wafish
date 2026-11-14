#!/usr/bin/env python3
"""Pack public libraries, validate APIs, and execute consumers outside repository build imports."""
from pathlib import Path
import argparse,json,os,shutil,subprocess,tempfile,zipfile,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser();parser.add_argument('--update-api',action='store_true');parser.add_argument('--skip-pack',action='store_true');args=parser.parse_args()
packages=root/'artifacts/package-checks';packages.mkdir(parents=True,exist_ok=True)
def run(command,**kwargs):subprocess.run(command,check=True,timeout=240,**kwargs)
if not args.skip_pack:
 for project in sorted((root/'src').glob('*/*.csproj')):
  run(['dotnet','pack',str(project),'-c','Release','--no-build','-o',str(packages)],cwd=root)
with tempfile.TemporaryDirectory(prefix='wafish-package-consumers-') as temporary:
 temp=Path(temporary);assemblies=temp/'assemblies';assemblies.mkdir()
 for package in packages.glob('*.nupkg'):
  with zipfile.ZipFile(package) as archive:
   for name in archive.namelist():
    if name.startswith('lib/net10.0/') and name.endswith('.dll'):(assemblies/Path(name).name).write_bytes(archive.read(name))
 run(['dotnet','run','--project',str(root/'tests/WafIsh.PackageChecks'),'-c','Release','--',str(assemblies),str(root/'tests/contracts/public-api')]+(['--update'] if args.update_api else []),cwd=root)
 version=ET.parse(root/'src/Directory.Build.props').find('.//Version').text
 for profile,extra in [('runtime',[]),('admin',['WafIsh.Administration']),('ui',['WafIsh.ReactUi'])]:
  work=temp/profile;work.mkdir();shutil.copyfile(root/'tests/PackageConsumer/Program.cs',work/'Program.cs')
  refs=''.join(f'<PackageReference Include="{name}" Version="{version}" />' for name in ['WafIsh.Runtime','WafIsh.Storage.Sqlite']+extra)
  (work/'Consumer.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><DefineConstants>{profile.upper()}</DefineConstants></PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>')
  (work/'NuGet.Config').write_text(f'''<configuration><packageSources><clear/><add key="local" value="{packages.as_posix()}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local"><package pattern="WafIsh*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>''')
  env=dict(os.environ,NUGET_PACKAGES=str(temp/'cache'))
  run(['dotnet','run','--project','Consumer.csproj','-c','Release'],cwd=work,env=env)
