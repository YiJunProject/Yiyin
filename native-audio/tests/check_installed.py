"""Isolated scan and offline consumer. No device or production process is opened."""
import json,pathlib,subprocess,queue,threading,time,math,hashlib,sys,os
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
out=root/('evidence/v0130-native-audio/installed-consumer-recheck' if len(sys.argv)>1 else 'evidence/v0130-native-audio/installed-consumer');out.mkdir(exist_ok=True)
if len(sys.argv)>1 and sys.argv[1] in ('vst2','fabpro'):out=root/('evidence/v0130-native-audio/installed-'+sys.argv[1]);out.mkdir(exist_ok=True)
if os.environ.get('YIDECK_AUDIT_LABEL'):out=root/'evidence/v0130-native-audio'/os.environ['YIDECK_AUDIT_LABEL'];out.mkdir(exist_ok=True)
engine=root/'native-audio/bin/Yideck.Audio.Engine.exe';scan=engine.with_name('Yideck.Audio.Scan.exe')
paths=[pathlib.Path('C:/Program Files/Common Files/VST3/FabFilter/FabFilter Micro.vst3'),pathlib.Path('C:/Program Files/Common Files/VST3/ValhallaDSP/ValhallaRoom.vst3')]
paths+=list(pathlib.Path('C:/Program Files/Common Files/VST3').glob('WaveShell*.vst3'))
paths+=list(pathlib.Path('C:/Program Files/Common Files/VST3/FabFilter').glob('FabFilter Pro-*.vst3'))
if len(sys.argv)>1:
 if sys.argv[1]=='final':paths=[p for p in paths if p.name in ['FabFilter Pro-C 2.vst3','FabFilter Pro-Q 3.vst3','FabFilter Pro-L 2.vst3','ValhallaRoom.vst3','WaveShell1-VST3 15.5_x64.vst3']]+[pathlib.Path('C:/Program Files/Steinberg/VSTPlugins/ReLab/LX480Complete.dll')]
 elif sys.argv[1]=='fabpro':paths=[p for p in paths if p.name in ['FabFilter Pro-C 2.vst3','FabFilter Pro-Q 3.vst3','FabFilter Pro-L 2.vst3']]
 elif sys.argv[1]=='vst2':paths=[pathlib.Path('C:/Program Files/Steinberg/VSTPlugins/ReLab/LX480Complete.dll'),pathlib.Path('C:/Program Files/VSTPlugIns/WaveShell1-VST 14.21_x64.dll')]
 else:paths=[p for p in paths if sys.argv[1].lower() in p.name.lower()]
report={'scope':'Independent child processes; actual installed plugins; synthetic offline float processing only; no audio endpoint opened', 'engineSha256':hashlib.sha256(engine.read_bytes()).hexdigest(),'plugins':[]}
for path in paths:
 row={'file':path.name};report['plugins'].append(row);p=None
 try:
  r=subprocess.run([str(scan),str(path)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=30)
  (out/(path.stem+'-scan.stderr.log')).write_bytes(r.stderr)
  found=json.loads(r.stdout);row['scan']=found
  if not found.get('ok'):continue
  effects=found['plugins'];candidate=next((x for x in effects if 'stereo' in x['name'].lower()),effects[0]);row['selected']=candidate['name']
  log=open(out/(path.stem+'-engine.stderr.log'),'w',encoding='utf-8')
  p=subprocess.Popen([str(engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8')
  q=queue.Queue();threading.Thread(target=lambda proc=p:[q.put(x) for x in proc.stdout],daemon=True).start();seq=0
  def ask(op,**kw):
   global seq
   seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(q.get(timeout=20))
   if not r['ok']:raise RuntimeError(str(r))
   return r['result']
  plug=dict(instanceId='actual',format=candidate['format'],classId=candidate['classId'],path=str(path))
  graph={'nodes':[{'id':'in','kind':'input','channels':2,'plugins':[plug]},{'id':'out','kind':'output','channels':2}], 'edges':[{'id':'main','from':'in','to':'out'}]}
  row['graph']=ask('setGraph',graph=graph);info=ask('pluginInfo',instanceId='actual');row['parameterCount']=len(info['parameters']);s=ask('pluginState',instanceId='actual');row['stateSizes']={k:len(v) if isinstance(v,(str,list)) else 0 for k,v in s.items()}
  square=0.;peak=0.;nonfinite=0;t=time.monotonic()
  for block in range(188):
   a=[.01*math.sin((block*256+i)*2*math.pi*440/48000) for i in range(256) for c in range(2)]
   result=ask('offline',frames=256,input=a)['samples'];square+=sum(x*x for x in result if math.isfinite(x));peak=max(peak,max(abs(x) for x in result));nonfinite+=sum(not math.isfinite(x) for x in result)
  row['process']={'frames':188*256,'peak':peak,'rms':math.sqrt(square/(188*512)),'nonFinite':nonfinite,'wallSeconds':time.monotonic()-t}
  row['snapshotAfterProcessing']=bool(ask('pluginState',instanceId='actual'));ask('setPluginState',instanceId='actual',state=s);row['restore']=True
  ask('shutdown');p.stdin.close();p.wait(timeout=10);row['exitCode']=p.returncode;row['passed']=peak>0 and nonfinite==0 and p.returncode==0
 except Exception as e:row['error']=str(e);row['passed']=False
 finally:
  if p is not None and p.poll() is None:p.kill();p.wait()
  (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
 print(path.name,'scan',row.get('scan',{}).get('ok'),'classes',len(row.get('scan',{}).get('plugins',[])),'process',row.get('passed'),'error',row.get('error',''),flush=True)
