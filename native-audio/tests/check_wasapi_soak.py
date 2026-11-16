"""Task-owned WASAPI shared stream, digital silence only. Never opens capture or changes endpoint settings."""
import sys,pathlib,subprocess,json,time,hashlib,shutil,threading,queue
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];seconds=int(sys.argv[1]) if len(sys.argv)>1 else 1800
out=root/'evidence/v0130-native-audio/wasapi-soak';out.mkdir(exist_ok=True)
for name in ['Yideck.Audio.Engine.exe','Yideck.Audio.Scan.exe']:shutil.copy2(root/'native-audio/bin'/name,out/name)
exe=out/'Yideck.Audio.Engine.exe';fixture=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=open(out/'stderr.log','w',encoding='utf-8'),encoding='utf-8',text=True)
q=queue.Queue();threading.Thread(target=lambda:[q.put(x) for x in p.stdout],daemon=True).start();sequence=0
def ask(op,**kw):
 global sequence
 sequence+=1;p.stdin.write(json.dumps(dict(id=sequence,op=op,**kw))+'\n');p.stdin.flush()
 r=json.loads(q.get(timeout=15))
 if not r.get('ok'):raise RuntimeError(str(r))
 return r['result']
report=dict(scope='isolated shared WASAPI output only; digital silence; no physical acoustic/loopback proof; no capture opened',secondsRequested=seconds,sha256=hashlib.sha256(exe.read_bytes()).hexdigest(),samples=[])
try:
 devices=ask('devices')['devices'];device=next((d for d in devices if 'minifuse' in d['name'].lower() and 'Main' in d['name'] and d['outputChannels']>=2 and d['preferredSampleRate'] in (44100,48000)),None)
 if device is None:raise RuntimeError('No explicitly matched MiniFuse Main output; no fallback device opened')
 report['deviceName']=device['name'];report['endpointIdSha256']=hashlib.sha256(device['id'].encode()).hexdigest()
 desc=json.loads(subprocess.check_output([str(out/'Yideck.Audio.Scan.exe'),str(fixture)],encoding='utf-8',timeout=15))['plugins'][0]
 ask('configure',api='wasapi',inputChannels=0,outputChannels=device['outputChannels'],outputDeviceId=device['id'],sampleRate=device['preferredSampleRate'],bufferFrames=256)
 graph=dict(nodes=[dict(id='silence',kind='media',channels=2,plugins=[dict(instanceId='test',format='vst3',path=str(fixture),classId=desc['classId'])]),dict(id='out',kind='output',channels=2,deviceChannels=[0,1])],edges=[{'id':'main','from':'silence','to':'out'}])
 ask('setGraph',graph=graph);report['start']=ask('start');t=time.monotonic();print('Started isolated silent WASAPI soak',device['name'],seconds,'seconds',flush=True)
 polls=0;snapshots=0
 while time.monotonic()-t<seconds:
  time.sleep(min(.1,max(.01,seconds-(time.monotonic()-t))));s=ask('status');polls+=1
  ask('setControls',nodes=[{'id':'silence','gain':.25}],edges=[{'id':'main','gain':1}])
  if polls%10==0:ask('pluginState',instanceId='test');snapshots+=1
  report['elapsedSeconds']=time.monotonic()-t;report['statusPolls']=polls;report['runningStateCaptures']=snapshots
  if polls%100==0 or report['elapsedSeconds']>=seconds:
   report['samples'].append(s);(out/'progress.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
  if not s['running'] or s['processedFrames']==0:raise RuntimeError('WASAPI callback not progressing')
  if s['gateSkipCount']:raise RuntimeError('Routine status/controls/state interrupted callback')
 report['stop']=ask('stop');report['shutdown']=ask('shutdown');p.stdin.close();p.wait(timeout=10);report['exitCode']=p.returncode;report['passed']=p.returncode==0
except Exception as e:report['passed']=False;report['error']=str(e);raise
finally:
 if p.poll() is None:p.kill();p.wait()
 (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('WASAPI silent soak finished:',out/'report.json',flush=True)
