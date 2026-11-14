from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='wafish-api-negative-') as temporary:
 p=Path(temporary)
 (p/'Wafish.Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
 source=p/'Contract.cs';source.write_text('public class Contract { public void RequiredMethod() {} }')
 def build():subprocess.run(['dotnet','build','-c','Release','--nologo'],cwd=p,check=True,capture_output=True)
 check=['dotnet',str(root/'tests/Wafish.PackageChecks/bin/Release/net10.0/Wafish.PackageChecks.dll'),str(p/'bin/Release/net10.0'),str(p/'baseline')]
 build();subprocess.run(check+['--update'],check=True,capture_output=True)
 source.write_text('public class Contract { }');build()
 failed=subprocess.run(check,text=True,capture_output=True)
 assert failed.returncode==1 and 'RequiredMethod' in failed.stderr, failed.stdout+failed.stderr
 print('PASS: removing a public API fails compatibility validation')


 baseline = 'public interface IResource { void Start(); void Stop() {} } public record Snapshot(string Name);'
 source.write_text(baseline);build();subprocess.run(check+['--update'],check=True,capture_output=True)
 cases = [
  ('new abstract interface member', baseline.replace('void Start();', 'void Start(); void Restart();'), False, 'new abstract requirement'),
  ('new default interface member', baseline.replace('void Start();', 'void Start(); void Restart() {}'), True, ''),
  ('record constructor change', baseline.replace('Snapshot(string Name)', 'Snapshot(int Name)'), False, 'removed or changed'),
  ('new interface type', baseline + ' public interface INewResource { void Start(); }', True, ''),
 ]
 for name, contents, succeeds, diagnostic in cases:
  source.write_text(contents);build()
  result=subprocess.run(check,text=True,capture_output=True)
  assert (result.returncode==0)==succeeds and diagnostic in result.stderr, result.stdout+result.stderr
  print('PASS: '+name+' has the expected compatibility result')
 (p/'bin/Release/net10.0/Wafish.Probe.dll').unlink()
 failed=subprocess.run(check,text=True,capture_output=True)
 assert failed.returncode==1 and 'Missing package assembly: Wafish.Probe' in failed.stderr, failed.stdout+failed.stderr
 print('PASS: removing a package assembly fails compatibility validation')
