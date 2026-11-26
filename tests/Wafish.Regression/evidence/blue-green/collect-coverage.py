import runpy,sys,threading,trace,pathlib,json
script=str(pathlib.Path('scripts/check-blue-green.py').resolve())
tracer=trace.Trace(count=True,trace=False,ignoredirs=[sys.prefix,sys.exec_prefix])
threading.settrace(tracer.globaltrace)
tracer.runfunc(runpy.run_path,script,run_name='__main__')
threading.settrace(None)
lines={line for line in trace._find_executable_linenos(script) if isinstance(line,int) and line>0}
hits={line for (file,line),count in tracer.results().counts.items() if file==script and count>0}
report={'file':'scripts/check-blue-green.py','covered':len(hits&lines),'executable':len(lines),'uncovered':sorted(lines-hits)}
report['percent']=round(report['covered']/report['executable']*100,2)
pathlib.Path('/tmp/wafish-r50-coverage.json').write_text(json.dumps(report,indent=2)+'\n')
assert report['percent']>=80,report
