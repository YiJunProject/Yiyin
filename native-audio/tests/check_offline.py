import json, subprocess, pathlib, sys, mmap, struct, uuid, hashlib, argparse
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--engine',type=pathlib.Path,default=root/'native-audio/bin/Yideck.Audio.Engine.exe')
parser.add_argument('--output',type=pathlib.Path,default=root/'evidence/v0140-native-multistream/legacy-offline-report.json')
parser.add_argument('--expected-asio',action='store_true')
args=parser.parse_args();exe=args.engine
fixture=root/'native-audio/test-bin/Yideck.Test.VST2.dll'
out=args.output
out.parent.mkdir(parents=True, exist_ok=True)
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
checks=[];seq=0
def request(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush()
 line=p.stdout.readline()
 if not line: raise RuntimeError('Engine exited: '+p.stderr.read())
 return json.loads(line)
def good(op,**kw):
 r=request(op,**kw)
 if not r['ok']:raise RuntimeError(str(r))
 return r['result']
def check(name,value,detail=None):
 checks.append(dict(name=name,pass_=bool(value),detail=detail));assert value,name
def node(id,kind,channels=2,**kw):return dict(id=id,kind=kind,channels=channels,**kw)
def edge(f,t,**kw):return dict(from_=f,to=t,**kw)
def edges(*items):return [dict({'from':f,'to':t},**kw) for f,t,kw in items]
def audio(n=64):
 a=[0.]*(n*2);a[0]=1;a[1]=.25;return a
try:
 cap=good('ping')['capabilities'];check('Expected ASIO build capability',cap['asio']==args.expected_asio)
 scan=json.loads(subprocess.check_output([str(exe.with_name('Yideck.Audio.Scan.exe')),str(fixture)],encoding='utf-8',timeout=15));check('Separate scanner reads real DLL',scan['ok'] and len(scan['plugins'])==1)
 desc=scan['plugins'][0];plug=dict(instanceId='test',format='vst2',path=str(fixture),classId=desc['classId'])
 g=dict(nodes=[node('input','input'),node('fx','fx',plugins=[plug]),node('out','output')],edges=edges(('input','fx',{}),('fx','out',{})))
 good('setGraph',graph=g);r=good('offline',frames=64,input=audio())['samples'];check('Actual VST2 process gain and latency',abs(r[34]-.5)<1e-7 and abs(r[35]-.125)<1e-7 and all(v==0 for v in r[:34]))
 state=good('pluginState',instanceId='test');good('setParameter',instanceId='test',parameterId=0,value=.75);check('Parameter reaches plugin',struct.unpack('<f',__import__('base64').b64decode(good('pluginState',instanceId='test')['chunk']))[0]==.75)
 good('setPluginState',instanceId='test',state=state);check('Opaque plugin state restores',good('pluginState',instanceId='test')==state)
 g['edges']+=edges(('input','out',{}));good('setGraph',graph=g);r=good('offline',frames=64,input=audio())['samples'];check('Parallel dry and plugin PDC aligns impulse',abs(r[34]-1.5)<1e-7 and sum(abs(v)>1e-7 for v in r)==2)
 cycle=dict(nodes=g['nodes'],edges=g['edges']+edges(('out','input',{})));check('Cycles rejected',not request('setGraph',graph=cycle)['ok']);check('Failed graph preserves prior graph',good('status')['graphLatencyFrames']==17)
 bad=dict(nodes=[node('bad','fx',plugins=[dict(plug,path='missing.dll')])],edges=[]);check('Missing plugin explicitly fails',not request('setGraph',graph=bad)['ok'])
 vst3=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
 scan3=json.loads(subprocess.check_output([str(exe.with_name('Yideck.Audio.Scan.exe')),str(vst3)],encoding='utf-8',timeout=15));check('Official SDK VST3 factory scanned in UTF8 bundle path',scan3['ok'] and len(scan3['plugins'])==1)
 p3=dict(instanceId='v3',format='vst3',path=str(vst3),classId=scan3['plugins'][0]['classId'])
 g3=dict(nodes=[node('in','input'),node('effect','fx',plugins=[p3]),node('out','output')],edges=edges(('in','effect',{}),('effect','out',{})))
 good('setGraph',graph=g3);r=good('offline',frames=64,input=audio())['samples'];check('Actual VST3 processor gain and latency',abs(r[34]-.5)<1e-7 and abs(r[35]-.125)<1e-7)
 s3=good('pluginState',instanceId='v3');good('setParameter',instanceId='v3',parameterId=0,value=.75);r=good('offline',frames=64,input=audio())['samples'];check('VST3 normalized parameter queue reaches processor',abs(r[34]-.75)<1e-7)
 good('setPluginState',instanceId='v3',state=s3);r=good('offline',frames=64,input=audio())['samples'];check('VST3 full state restores component/controller',abs(r[34]-.5)<1e-7)
 good('setParameter',instanceId='v3',parameterId=0,value=.25);good('offline',frames=64)
 g3['nodes'][1]['plugins'][0]['state']=s3;g3['nodes'].append(node('unused','bus'))
 good('setGraph',graph=g3);r=good('offline',frames=64,input=audio())['samples'];check('Graph edit preserves existing plugin live state over stale serialized state',abs(r[34]-.25)<1e-7)
 good('setParameter',instanceId='v3',parameterId=0,value=.625)
 check('Stopped VST3 parameter is flushed before snapshot without device start',struct.unpack('<f',__import__('base64').b64decode(good('pluginState',instanceId='v3')['component']))[0]==.625)
 good('clearGraph');wet=dict(plug,wet=.25);gw=dict(nodes=[node('in','input',plugins=[wet]),node('out','output')],edges=edges(('in','out',{})))
 good('setGraph',graph=gw);r=good('offline',frames=64,input=audio())['samples'];check('Plugin wet blends latency-aligned dry and wet samples',abs(r[34]-.875)<1e-7)
 gw['nodes'][0]['plugins'][0]['bypass']=True;good('setGraph',graph=gw);r=good('offline',frames=64,input=audio())['samples'];check('Plugin bypass preserves its PDC latency',r[34]==1 and all(x==0 for x in r[:34]))
 gp=dict(nodes=[node('in','input',gain=.25),node('out','output')],edges=[{'id':'pre','from':'in','to':'out','source':'pre'}]);good('setGraph',graph=gp);r=good('offline',frames=64,input=audio())['samples'];check('Pre-fader send ignores source fader gain',r[0]==1)
 good('setControls',nodes=[{'id':'in','mute':True}]);r=good('offline',frames=64,input=audio())['samples'];check('Source mute also blocks pre-fader send',all(x==0 for x in r))
 good('setControls',nodes=[{'id':'in','mute':False}])
 good('setControls',nodes=[{'id':'in','monitor':False}]);r=good('offline',frames=64,input=audio())['samples'];check('Monitor-off disconnects physical input before sends',all(x==0 for x in r))
 initial=dict(nodes=[node('in','input'),node('out','output',mute=True)],edges=edges(('in','out',{})));good('setGraph',graph=initial);r=good('offline',frames=64,input=audio())['samples'];check('Initially muted output cannot leak first-block audio',all(x==0 for x in r))
 initial['nodes'][1]['mute']=False;initial['nodes'][1]['gain']=.25;good('setGraph',graph=initial);r=good('offline',frames=64,input=audio())['samples'];check('Prepared fader starts at actual gain rather than unity',r[0]==.25)
 gd=dict(nodes=[node('in','input'),node('out','output')],edges=[{'id':'main','from':'in','to':'out','gain':1},{'id':'send','from':'in','to':'out','gain':.5}])
 good('setGraph',graph=gd);good('setControls',edges=[{'id':'send','gain':.25}]);r=good('offline',frames=64,input=audio())['samples'];check('Same-endpoint main and send gains remain independent by edge id',r[0]==1.25)
 check('Missing exact control edge fails',not request('setControls',edges=[{'id':'missing','gain':1}])['ok'])
 good('setControls',edges=[{'id':'main','gain':0}]);r=good('offline',frames=64,input=audio())['samples'];check('Main edge update does not overwrite post-fader send',r[0]==.25)
 g2=dict(nodes=[node('mono','input',1,deviceChannels=[0],pan=0),node('out','output')],edges=edges(('mono','out',{'channels':1})));good('setGraph',graph=g2);r=good('offline',frames=64,input=audio())['samples'];check('Mono expands to stereo',r[0]==1 and r[1]==1)
 good('setControls',nodes=[dict(id='mono',pan=1)]);r=good('offline',frames=64,input=audio())['samples'];check('Mono pan right maps only right',r[0]==0 and r[1]==1)
 name='Local\\YideckAudio-Test-'+str(uuid.uuid4());capacity=4096
 with mmap.mmap(-1,128+capacity*8,tagname=name) as mm:
  struct.pack_into('<6I',mm,0,0x59415231,1,capacity,2,48000,0)
  good('attachMedia',name=name);good('resetMedia',generation=5)
  gm=dict(nodes=[node('media','media'),node('out','output')],edges=edges(('media','out',{})));good('setGraph',graph=gm)
  for i in range(64):struct.pack_into('<ff',mm,128+i*8,.125,-.25)
  struct.pack_into('<Q',mm,24,64);r=good('offline',frames=64)['samples'];check('Shared memory stereo PCM consumed',r[0]==.125 and r[1]==-.25 and struct.unpack_from('<Q',mm,48)[0]==64)
  check('Stale detach generation rejected',not good('detachMedia',generation=4)['detached']);check('Current generation detaches',good('detachMedia',generation=5)['detached'])
 good('clearGraph');good('configure',inputChannels=0,outputChannels=2,sampleRate=44100,bufferFrames=256)
 with mmap.mmap(-1,128+capacity*8,tagname=name+'44100') as mm:
  struct.pack_into('<6I',mm,0,0x59415231,1,capacity,2,48000,0)
  check('Mismatched media rate fails without implicit realtime resampling',not request('attachMedia',name=name+'44100')['ok'])
  struct.pack_into('<I',mm,16,44100);good('attachMedia',name=name+'44100');good('resetMedia',generation=6);good('setGraph',graph=gm)
  for i in range(64):struct.pack_into('<ff',mm,128+i*8,.125,-.25)
  struct.pack_into('<Q',mm,24,64);r=good('offline',frames=64)['samples'];check('44100 stereo mapping consumed at configured rate',r[0]==.125 and struct.unpack_from('<Q',mm,56)[0]==64)
  good('detachMedia',generation=6)
 check('Offline status and controls never gate callbacks',good('status')['gateSkipCount']==0)
 check('Shutdown acknowledges',good('shutdown')['stopped'])
 p.stdin.close();p.wait(timeout=8);check('Owned engine exits',p.returncode==0)
finally:
 if p.poll() is None:p.kill();p.wait()
 out.write_text(json.dumps(dict(passed=sum(x['pass_'] for x in checks),total=len(checks),checks=checks,sha256=hashlib.sha256(exe.read_bytes()).hexdigest(),scope='offline isolated real VST2 DLL and shared-memory consumer; no device opened'),ensure_ascii=False,indent=2),encoding='utf-8')
print(out, len(checks), 'PASS')
