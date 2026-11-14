import xml.etree.ElementTree as E, subprocess,re, pathlib, sys
root=pathlib.Path.cwd(); files={}
diff=subprocess.check_output(['git','diff','--unified=0','--src-prefix=a/','--dst-prefix=b/','--','src'],text=True)
p=None; n=0
for line in diff.splitlines():
 if line.startswith('+++ b/'): p=str(root/line[6:]); files.setdefault(p,set())
 elif line.startswith('@@'): n=int(re.search(r'\+(\d+)',line).group(1))
 elif line.startswith('+') and p: files[p].add(n); n+=1
 elif line.startswith(' ') and p: n+=1
for p in subprocess.check_output(['git','ls-files','--others','--exclude-standard','--','src'],text=True).splitlines():
 if p.endswith('.cs'): files[str(root/p)]=set(range(1,len(pathlib.Path(p).read_text().splitlines())+1))
covered={}; measured={}
for m in E.parse(sys.argv[1]).getroot().findall('.//module'):
 sources={s.get('id'):s.get('path') for s in m.findall('.//source_file')}
 for r in m.findall('.//range'):
  p=sources.get(r.get('source_id'))
  if p not in files: continue
  lines=set(range(int(r.get('start_line')), int(r.get('end_line'))+1)) & files[p]
  measured.setdefault(p,set()).update(lines)
  if r.get('covered')=='yes': covered.setdefault(p,set()).update(lines)
num=den=0
for p, lines in measured.items():
 yes=len(lines & covered.get(p,set())); num+=yes; den+=len(lines)
 print(f'{yes}/{len(lines)} {p.removeprefix(str(root)+"/")} missing {sorted(lines-covered.get(p,set()))}')
print('TOTAL',num,den,round(100*num/den,2))
