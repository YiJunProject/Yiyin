"""Offline real-engine checks for float headroom, final-mix observation and live insert/send controls."""
import argparse,hashlib,json,math,pathlib,subprocess,sys
sys.stdout.reconfigure(encoding='utf-8');root=pathlib.Path(__file__).resolve().parents[2]
ap=argparse.ArgumentParser();ap.add_argument('--engine',type=pathlib.Path,default=root/'native-audio/bin-v0151/Yideck.Audio.Engine.exe');ap.add_argument('--output',type=pathlib.Path,default=root/'evidence/v0151-native/offline-report.json');a=ap.parse_args();a.output.parent.mkdir(parents=True,exist_ok=True)
p=subprocess.Popen([str(a.engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8');seq=0;checks=[]
def ask(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(p.stdout.readline())
 if not r['ok']:raise RuntimeError(r)
 return r['result']
def check(v,name,detail=None):checks.append(dict(name=name,passed=bool(v),detail=detail))
def rejected(op,**kw):
 try:ask(op,**kw);return False
 except RuntimeError:return True
def node(id,kind,**kw):return dict(id=id,kind=kind,channels=2,**kw)
def edge(id,origin,target,**kw):return dict(id=id,**{'from':origin,'to':target},**kw)
def graph(nodes,edges):ask('setGraph',graph=dict(nodes=nodes,edges=edges))
def run(l,r,n=128):return ask('offline',frames=n,input=[v for _ in range(n) for v in [l,r]])['samples']
def meter(id):return next(x for x in ask('meters')['meters'] if x['id']==id)
report=dict(engineSha256=hashlib.sha256(a.engine.read_bytes()).hexdigest(),hardwareOpened=False)
try:
 graph([node('in','input',gain=2),node('sum','bus'),node('out','output',gain=.25)],[edge('route1','in','sum'),edge('route2','in','sum'),edge('sum:main','sum','out')])
 samples=run(.375,-.4375);m=meter('sum')
 check(m['peakLeft']==1.5 and m['peakRight']==1.75,'Float graph summing retains values above 0 dBFS',m)
 check(samples==[v for _ in range(128) for v in [.375,-.4375]],'Downstream attenuation exactly restores original signal after internal overscale')
 check(m['clipped'] and samples[0]!=.25,'Existing clipped meter flag means above nominal full scale, not actual internal clipping')
 graph([node('in','input'),node('out','output')],[edge('in:main','in','out')]);samples=run(8,-16)
 check(samples[0]==8 and samples[1]==-16,'Offline float output does not clamp large finite signal to plus/minus one')
 for fmt,path in [('vst2',root/'native-audio/test-bin-v0150/Yideck.Test.VST2.dll'),('vst3',root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3')]:
  desc=json.loads(subprocess.check_output([str(a.engine.with_name('Yideck.Audio.Scan.exe')),str(path)],encoding='utf-8'))['plugins'][0]
  plug=dict(instanceId='plugin-'+fmt,format=fmt,path=str(path),classId=desc['classId'])
  ask('clearGraph');graph([node('in','input'),node('fx','fx',plugins=[plug]),node('out','output')],[edge('i','in','fx'),edge('f','fx','out')])
  samples=run(3,-3.5);check(samples[34]==1.5 and samples[35]==-1.75,fmt+' actual float plugin receives and returns overscale samples unchanged except declared gain')
  ask('setControls',nodes=[dict(id='out',gain=.25)]);run(3,-3.5);samples=run(3,-3.5)
  check(samples[0]==.375 and samples[1]==-.4375,fmt+' overscale plugin output can be attenuated downstream without host clipping')
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],wet=0)])]);samples=run(3,-3.5)
  check(samples[64]==.75 and samples[65]==-.875,fmt+' wet changes at block boundary without rebuilding graph')
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],wet=1,bypass=True)])]);samples=run(3,-3.5)
  check(samples[64]==.75 and samples[65]==-.875,fmt+' bypass changes live and retains dry PDC')
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],bypass=False)])]);samples=run(3,-3.5)
  check(samples[64]==.375 and samples[65]==-.4375,fmt+' partial insert control preserves desired wet and restores processing')
  check(ask('status')['graphLatencyFrames']==17,fmt+' wet/bypass control retains prepared 17-frame PDC')
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],wet=0)])])
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],bypass=True)])])
  ask('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],bypass=False)])])
  samples=run(3,-3.5);check(samples[64]==.75 and samples[65]==-.875,fmt+' queued partial controls preserve desired wet before audio consumes them')
  check(rejected('setControls',nodes=[dict(id='fx',plugins=[dict(instanceId=plug['instanceId'],wet=2)])]),fmt+' invalid wet rejected without silent clamping')
 ask('clearGraph');graph([node('in','input',gain=.25),node('out','output')],[edge('send','in','out',source='post')])
 check(run(1,1)[0]==.25,'Post send initially follows source fader')
 ask('setControls',edges=[dict(id='send',gain=1,source='pre')]);check(run(1,1)[0]==1,'Live pre/post switch reaches next audio block')
 ask('setControls',edges=[dict(id='send',gain=.5)]);check(run(1,1)[0]==.5,'Partial send gain update preserves selected pre tap')
 ask('setControls',edges=[dict(id='send',gain=.75)])
 ask('setControls',edges=[dict(id='send',source='post')]);check(run(1,1)[0]==.1875,'Queued tap-only update preserves pending gain')
 check(rejected('setControls',nodes=[dict(id='in',gain=.5)],edges=[dict(id='send',source='invalid')]),'Invalid tap rejects complete batch before publishing controls')
 check(run(1,1)[0]==.1875,'Rejected mixed control batch leaves prior gain unchanged')
 ask('setControls',edges=[dict(id='send',source='pre')])
 ask('setControls',nodes=[dict(id='in',mute=True)]);check(all(v==0 for v in run(1,1)),'Track mute still suppresses live pre-fader send')
 # Two individually in-range output tracks sum onto one hardware channel pair.
 ask('clearGraph');graph([node('in','input'),node('a','output'),node('b','output')],[edge('a-route','in','a'),edge('b-route','in','b')]);samples=run(.75,-.75)
 m=ask('meters')['meters'];check(all(x['peakLeft']==.75 for x in m),'Individual output track meters can remain below 0 dBFS')
 check(samples[0]==1.5 and samples[1]==-1.5,'Final shared hardware-channel mix can exceed 0 dBFS')
 final=ask('status');signal=final.get('outputSignal',{})
 check(signal.get('peakSinceReset',0)>=1.5 and signal.get('overRangeSamples',0)>0,'Final mixed output over-range is observed independently of per-track meters',signal)
 check(final['callbackCount']==0,'No hardware callback or physical endpoint opened')
 ask('clearGraph');ask('configure',inputChannels=0,outputChannels=2,sampleRate=48000,bufferFrames=256)
 reset=ask('status')['outputSignal'];check(reset['peakSinceReset']==0 and reset['overRangeSamples']==0 and reset['framesObserved']==0,'Successful stopped configure resets final-output diagnosis')
 ask('shutdown');p.stdin.close();p.wait(10);check(p.returncode==0,'Owned offline engine exits cleanly')
 report['passed']=all(x['passed'] for x in checks)
except Exception as e:report.update(passed=False,error=str(e))
finally:
 if p.poll() is None:p.kill();p.wait()
 report.update(checks=checks,checkCount=len(checks),passedCount=sum(x['passed'] for x in checks),stderr=p.stderr.read());a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['checks','stderr']},ensure_ascii=False));sys.exit(0 if report['passed'] else 1)
