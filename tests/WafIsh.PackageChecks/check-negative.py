from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='wafish-api-negative-') as temporary:
 p=Path(temporary)
 (p/'WafIsh.Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
 source=p/'Contract.cs';source.write_text('public class Contract { public void RequiredMethod() {} }')
 def build():subprocess.run(['dotnet','build','-c','Release','--nologo'],cwd=p,check=True,capture_output=True)
 check=['dotnet',str(root/'tests/WafIsh.PackageChecks/bin/Release/net10.0/WafIsh.PackageChecks.dll'),str(p/'bin/Release/net10.0'),str(p/'baseline')]
 build();subprocess.run(check+['--update'],check=True,capture_output=True)
 source.write_text('public class Contract { }');build()
 failed=subprocess.run(check,text=True,capture_output=True)
 assert failed.returncode==1 and 'RequiredMethod' in failed.stderr, failed.stdout+failed.stderr
 print('PASS: removing a public API fails compatibility validation')

 (p/'bin/Release/net10.0/WafIsh.Probe.dll').unlink()
 failed=subprocess.run(check,text=True,capture_output=True)
 assert failed.returncode==1 and 'Missing package assembly: WafIsh.Probe' in failed.stderr, failed.stdout+failed.stderr
 print('PASS: removing a package assembly fails compatibility validation')
