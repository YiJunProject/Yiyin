"""Silent real WASAPI consumer for current VST3 state capture; no physical capture opened."""
import pathlib,subprocess,json,threading,queue,time,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];out=root/'evidence/v0130-native-audio/running-commercial-final';out.mkdir(exist_ok=True)
exe=root/'native-audio/bin/Yideck.Audio.Engine.exe'
sources=json.loads((root/'evidence/v0130-native-audio/installed-fabpro/report.json').read_text(encoding='utf-8'))['plugins']
original=json.loads((root/'evidence/v0130-native-audio/installed-consumer/report.json').read_text(encoding='utf-8'))['plugins']
sources+=[next(x for x in original if x.get('selected')==n) for n in ['ValhallaRoom','CLA-76 Stereo']]
plugins=[]
for ix,row in enumerate(sources):
 d=next(d for d in row['scan']['plugins'] if d['name']==row['selected']);plugins.append({'instanceId':str(ix),'format':'vst3','classId':d['classId'],'path':d['path']})
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=open(out/'stderr.log','w',encoding='utf-8'),text=True,encoding='utf-8');q=queue.Queue();threading.Thread(target=lambda:[q.put(x) for x in p.stdout],daemon=True).start();seq=0
def ask(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(q.get(timeout=20))
 if not r['ok']:raise RuntimeError(str(r))
 return r['result']
report={'engineSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'scope':'Digital silence through 5 actual VST3 effects and WASAPI output. No microphone capture. Functional concurrent state test, not an isolated performance benchmark.', 'plugins':[x['selected'] for x in sources]}
try:
 ds=ask('devices')['devices'];d=next(d for d in ds if 'minifuse' in d['name'].lower() and 'Main' in d['name'] and d['outputChannels']>=2)
 ask('configure',inputChannels=0,outputChannels=2,outputDeviceId=d['id'],sampleRate=d['preferredSampleRate'],bufferFrames=256)
 graph={'nodes':[{'id':'source','kind':'media','channels':2,'plugins':plugins},{'id':'out','kind':'output','channels':2,'gain':0,'mute':True}],'edges':[{'id':'main','from':'source','to':'out'}]}
 ask('setGraph',graph=graph);report['start']=ask('start');t=time.monotonic();captures={x['instanceId']:0 for x in plugins};maxStateMs=0
 while time.monotonic()-t<10:
  for plug in plugins:
   a=time.monotonic();state=ask('pluginState',instanceId=plug['instanceId']);maxStateMs=max(maxStateMs,(time.monotonic()-a)*1000);assert state.get('component');captures[plug['instanceId']]+=1
  ask('setControls',nodes=[{'id':'source','gain':.25}],edges=[{'id':'main','gain':1}]);s=ask('status');assert s['running'] and s['gateSkipCount']==0,s
  time.sleep(.05)
 report['finalStatus']=ask('status');report['captures']=captures;report['elapsedSeconds']=time.monotonic()-t;report['maxStateCallMs']=maxStateMs
 ask('stop');ask('shutdown');p.stdin.close();p.wait(timeout=10);report['passed']=p.returncode==0 and report['finalStatus']['processedFrames']>0
except Exception as e:report['passed']=False;report['error']=str(e);raise
finally:
 if p.poll() is None:p.kill();p.wait()
 (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('Current-build WASAPI + real commercial VST3 state captures:',report['passed'],captures)
