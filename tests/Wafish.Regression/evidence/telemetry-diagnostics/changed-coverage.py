import json,pathlib,re,subprocess,sys,xml.etree.ElementTree as ET
root=pathlib.Path.cwd()
base='accbc55'
changed={};path=None
for line in subprocess.check_output(['git','diff','--src-prefix=a/','--dst-prefix=b/','--unified=0',base,'--','src','samples','Wafish.AppHost'],text=True).splitlines():
    if line.startswith('+++ b/'):path=line[6:]
    elif path and path.endswith('.cs') and (m:=re.match(r'@@ .* \+(\d+)(?:,(\d+))? @@',line)):
        start=int(m[1]);length=int(m[2]) if m[2] is not None else 1
        changed.setdefault(str(root/path),set()).update(range(start,start+length))
measured={};covered={}
for report in sys.argv[1:]:
    for module in ET.parse(report).getroot().findall('.//module'):
        sources={s.get('id'):s.get('path') for s in module.findall('.//source_file')}
        for item in module.findall('.//range'):
            path=sources.get(item.get('source_id'))
            if path not in changed:continue
            lines=set(range(int(item.get('start_line')),int(item.get('end_line'))+1)) & changed[path]
            measured.setdefault(path,set()).update(lines)
            if item.get('covered')=='yes':covered.setdefault(path,set()).update(lines)
files=[]
for path,lines in sorted(measured.items()):
    hits=lines & covered.get(path,set())
    files.append(dict(file=str(pathlib.Path(path).relative_to(root)),covered=len(hits),executable=len(lines),uncovered=sorted(lines-hits)))
result=dict(base=base,covered=sum(x['covered'] for x in files),executable=sum(x['executable'] for x in files),files=files,missingFiles=sorted(set(changed)-set(measured)))
result['percent']=round(100*result['covered']/max(1,result['executable']),2)
print(json.dumps(result,indent=2))
if result['percent']<80 or result['missingFiles']:sys.exit(1)
