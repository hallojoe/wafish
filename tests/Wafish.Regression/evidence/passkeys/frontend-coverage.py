import json,pathlib,re,subprocess,sys
root=pathlib.Path.cwd();base='d6c5eb3';changed={};path=None
for line in subprocess.check_output(['git','diff','--src-prefix=a/','--dst-prefix=b/','--unified=0',base,'--','src/Wafish.ReactUi/ClientApp/src'],text=True).splitlines():
 if line.startswith('+++ b/'):path=line[6:]
 elif path and path.endswith(('.ts','.tsx')) and (m:=re.match(r'@@ .* \+(\d+)(?:,(\d+))? @@',line)):
  start=int(m[1]);count=int(m[2]) if m[2] is not None else 1
  changed.setdefault(path,set()).update(range(start,start+count))
measured={};covered={};path=None
for line in pathlib.Path(sys.argv[1]).read_text().splitlines():
 if line.startswith('SF:'):
  path=str(pathlib.Path('src/Wafish.ReactUi/ClientApp')/line[3:])
  if path in changed:measured.setdefault(path,set())
 elif line.startswith('DA:') and path in changed:
  number,hits=map(int,line[3:].split(',')[:2])
  if number in changed[path]:
   measured.setdefault(path,set()).add(number)
   if hits:covered.setdefault(path,set()).add(number)
files=[dict(file=p,covered=len(covered.get(p,set())),executable=len(lines),uncovered=sorted(lines-covered.get(p,set()))) for p,lines in sorted(measured.items())]
r=dict(base=base,files=files,covered=sum(x['covered'] for x in files),executable=sum(x['executable'] for x in files),missingFiles=sorted(set(changed)-set(measured)))
r['percent']=round(100*r['covered']/max(1,r['executable']),2)
print(json.dumps(r,indent=2))
if r['percent']<80 or r['missingFiles']:sys.exit(1)
