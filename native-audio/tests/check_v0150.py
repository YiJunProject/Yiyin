"""Real native JSONL consumer: interval meters, independent main gain, error attribution. No device opened."""
import argparse,hashlib,json,math,pathlib,subprocess,sys
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
ap=argparse.ArgumentParser();ap.add_argument('--engine',type=pathlib.Path,default=root/'native-audio/bin-v0150/Yideck.Audio.Engine.exe');ap.add_argument('--output',type=pathlib.Path,default=root/'evidence/v0150-native/offline-report.json');a=ap.parse_args()
a.output.parent.mkdir(parents=True,exist_ok=True)
p=subprocess.Popen([str(a.engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
seq=0;checks=[]
def request(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();return json.loads(p.stdout.readline())
def good(op,**kw):
 r=request(op,**kw)
 if not r['ok']:raise RuntimeError(r)
 return r['result']
def check(v,name):
 checks.append(dict(name=name,passed=bool(v)))
 if not v:raise AssertionError(name)
def node(id,kind,**kw):return dict(id=id,kind=kind,channels=kw.pop('channels',2),**kw)
def graph(nodes,edges):good('setGraph',graph=dict(nodes=nodes,edges=edges))
def meters():return {x['id']:x for x in good('meters')['meters']}
def run(left,right,n=64):return good('offline',frames=n,input=[v for _ in range(n) for v in [left,right]])['samples']
report={'engineSha256':hashlib.sha256(a.engine.read_bytes()).hexdigest(),'hardwareOpened':False}
try:
 check(good('ping')['capabilities']['intervalMeters'],'interval meter capability advertised')
 check(good('meters')=={'meters':[]},'empty graph lightweight response')
 graph([node('input','input'),node('out','output')],[dict(id='input:main',**{'from':'input','to':'out'})])
 pulse=[0.0]*128;pulse[0]=2.5;pulse[1]=-.75
 good('offline',frames=64,input=pulse)
 for _ in range(100):run(0,0)
 status=good('status');check(status['meters'][0]['peakLeft']==0,'status reports last block without clearing the interval')
 m=meters()['input'];check(m['peakLeft']==2.5 and m['peakRight']==.75 and m['clipped'],'short overscale burst retained across 100 silent blocks')
 check(m['sampleCount']==6464 and abs(m['rmsLeft']-2.5/math.sqrt(6464))<1e-12,'RMS includes all interval samples and silence')
 m=meters()['input'];check(m['sampleCount']==0 and m['rmsLeft']==0 and m['peakLeft']==0,'empty read returns zero not stale peak')
 samples=[v for i in range(240) for v in [.8*math.sin(i*2*math.pi/48),.5]]
 good('offline',frames=240,input=samples);good('status');m=meters()['input']
 check(abs(m['rmsLeft']-.8/math.sqrt(2))<1e-7 and m['rmsRight']==.5,'sine RMS and constant RMS match analytic values')
 run(.25,.5)
 graph([node('input','input'),node('out','output')],[dict(id='input:main',**{'from':'input','to':'out'})])
 run(.75,1.5);m=meters()['input'];check(m['sampleCount']==128 and m['peakRight']==1.5,'unchanged track meter survives graph replacement')
 check(abs(m['rmsLeft']-math.sqrt((.25**2+.75**2)/2))<1e-12,'replaced graph interval preserves weighted RMS')
 graph([node('source','input',gain=.5),node('main','output'),node('pre','bus'),node('post','bus')],[
  dict(id='source:main',**{'from':'source','to':'main'},gain=.25),
  dict(id='pre-send',**{'from':'source','to':'pre'},source='pre',gain=.75),
  dict(id='post-send',**{'from':'source','to':'post'},source='post',gain=.5)])
 out=run(1,1);m=meters();check(all(x==.125 for x in out),'main route gain is independent after track fader')
 check(m['pre']['peakLeft']==.75 and m['post']['peakLeft']==.25,'pre and post sends retain independent gain')
 good('setControls',edges=[dict(id='source:main',gain=0)]);out=run(1,1);m=meters()
 check(all(x==0 for x in out),'main route off silences only main path')
 check(m['pre']['peakLeft']==.75 and m['post']['peakLeft']==.25,'main off leaves enabled sends unchanged')
 good('setControls',nodes=[dict(id='source',mute=True)]);out=run(1,1);m=meters()
 check(all(x==0 for x in out) and m['pre']['peakLeft']==m['post']['peakLeft']==0,'track mute silences both pre and post sends')
 good('setControls',nodes=[dict(id='source',mute=False)],edges=[dict(id='source:main',gain=.5)])
 run(1,1);meters();out=run(1,1);m=meters();check(all(x==.25 for x in out) and m['pre']['peakLeft']==.75,'re-enabled main route restores independent stored gain')
 maxgain=10**(12/20)
 check(good('setControls',nodes=[dict(id='source',gain=maxgain)])=={},'+12dB track gain accepted')
 check(not request('setControls',nodes=[dict(id='source',gain=4)])['ok'],'track gain above +12dB rejected')
 check(not request('setControls',edges=[dict(id='source:main',gain=4)])['ok'],'main route gain above +12dB rejected')
 check(good('setControls',edges=[dict(id='pre-send',gain=4)])=={},'send gain retains its existing independent limit')
 graph([node('mono','input',channels=1,deviceChannels=[0])],[])
 run(.25,.75);m=meters()['mono'];check(m['peakLeft']==m['peakRight']==.25 and m['rmsLeft']==m['rmsRight']==.25,'mono meter mirrors one real channel')
 slot='17ce001e-2cec-44b2-a4a3-0f53acd8849c'
 r=request('setGraph',graph=dict(nodes=[node('failed','fx',plugins=[dict(instanceId=slot,format='vst3',path='Yideck-intentionally-missing.vst3',classId='missing')])],edges=[]))
 check(not r['ok'] and r.get('instanceId')==slot,'plugin load error identifies only the failed slot')
 check(good('status')['callbackCount']==0,'offline checks opened no physical stream')
 good('shutdown');p.stdin.close();p.wait(10);check(p.returncode==0,'owned engine exits cleanly');report['passed']=True
except Exception as e:report['passed']=False;report['error']=str(e)
finally:
 if p.poll() is None:p.kill();p.wait()
 report.update(checks=checks,checkCount=len(checks),stderr=p.stderr.read())
 a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['checks','stderr']}));sys.exit(0 if report.get('passed') else 1)
