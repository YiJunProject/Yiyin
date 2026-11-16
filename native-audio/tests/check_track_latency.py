"""Focused native track-latency contract; real fixture DLLs, no audio endpoint opened."""
import pathlib,subprocess,json,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];exe=root/'native-audio/bin/Yideck.Audio.Engine.exe'
fixture=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
out=root/'evidence/v0130-native-audio/track-latency-report.json'
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8');seq=0;checks=[]
def ask(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(p.stdout.readline())
 if not r['ok']:raise RuntimeError(str(r))
 return r['result']
def check(name,v):
 checks.append({'name':name,'passed':bool(v)});assert v,name
def insert(id,**kw):return dict(instanceId=id,format='vst3',path=str(fixture),classId='59494445434B46585445535430303031',**kw)
def values(s):return {n['id']:n['pluginLatencySamples'] for n in s['tracks']}
try:
 check('Unloaded graph has no misleading track latency',ask('status')['tracks']==[])
 ask('configure',sampleRate=44100,bufferFrames=256,inputChannels=0,outputChannels=2)
 graph={'nodes':[{'id':'input','kind':'input','channels':2},{'id':'fx','kind':'fx','channels':2,'plugins':[insert('a'),insert('b')]},{'id':'out','kind':'output','channels':2}], 'edges':[{'id':'in-fx','from':'input','to':'fx'},{'id':'fx-out','from':'fx','to':'out'},{'id':'dry','from':'input','to':'out'}]}
 ask('setGraph',graph=graph);s=ask('status');v=values(s)
 check('Two actual 17-sample VST3 inserts report cumulative 34 samples',v['fx']==34)
 check('No-plugin tracks exclude upstream and dry-route PDC',v['input']==0 and v['out']==0 and s['graphLatencyFrames']==34)
 check('Milliseconds use actual configured 44100 rate',s['sampleRate']==44100 and abs(v['fx']*1000/s['sampleRate']-0.7709750566893424)<1e-10)
 graph['nodes'][1]['plugins'][0]['bypass']=True;graph['nodes'][1]['plugins'][1]['wet']=0
 ask('setGraph',graph=graph);check('Host bypass and dry wet mix retain actual chain delay',values(ask('status'))['fx']==34)
 for _ in range(100):s=ask('status')
 check('Repeated status does not gate or open audio',s['gateSkipCount']==0 and not s['running'] and s['callbackCount']==0)
 ask('clearGraph');check('Cleared graph removes old latency entries',ask('status')['tracks']==[])
 ask('shutdown');p.stdin.close();p.wait(timeout=8)
finally:
 if p.poll() is None:p.kill();p.wait()
 out.write_text(json.dumps({'engineSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'scope':'Real VST3 fixture, offline status contract only; no device opened. Runtime invalidation branch is source-reviewed, not a fabricated plugin notification test.','passed':sum(c['passed'] for c in checks),'total':len(checks),'checks':checks},indent=2),encoding='utf-8')
print('Track latency',len(checks),'PASS')
