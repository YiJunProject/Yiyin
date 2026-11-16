"""Explicitly authorized 5s MiniFuse capture -> fixture -> forced mute audit. Never records PCM."""
import pathlib,subprocess,json,threading,queue,time,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];out=root/'evidence/v0130-native-audio/duplex-muted-final';out.mkdir(exist_ok=True)
exe=root/'native-audio/bin/Yideck.Audio.Engine.exe';fixture=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=open(out/'stderr.log','w',encoding='utf-8'),text=True,encoding='utf-8');q=queue.Queue();threading.Thread(target=lambda:[q.put(x) for x in p.stdout],daemon=True).start();seq=0
def ask(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(q.get(timeout=15))
 if not r['ok']:raise RuntimeError(str(r))
 return r['result']
report={'engineSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'scope':'Authorized 5-second same-card MiniFuse Input1 capture through actual VST3 fixture to initially muted output. No input PCM or input meter values recorded. Proves capture/render callback API, not external cabling or acoustic quality.'}
try:
 ds=ask('devices')['devices'];i=next(d for d in ds if 'minifuse' in d['name'].lower() and 'Input 1' in d['name'] and d['inputChannels']>=1);o=next(d for d in ds if 'minifuse' in d['name'].lower() and 'Main 1/2' in d['name'] and d['outputChannels']>=2)
 assert i['groupId'] and i['groupId']==o['groupId'];report['samePhysicalContainer']=True
 ask('configure',inputDeviceId=i['id'],outputDeviceId=o['id'],inputChannels=1,outputChannels=2,sampleRate=44100,bufferFrames=256)
 graph={'nodes':[{'id':'capture','kind':'input','channels':1,'deviceChannels':[0]},{'id':'fx','kind':'fx','channels':2,'plugins':[{'instanceId':'p','format':'vst3','path':str(fixture),'classId':'59494445434B46585445535430303031'}]},{'id':'out','kind':'output','channels':2,'deviceChannels':[0,1],'gain':0,'mute':True}], 'edges':[{'id':'mono-to-fx','from':'capture','to':'fx','channels':1},{'id':'main','from':'fx','to':'out'}]}
 ask('setGraph',graph=graph);report['start']=ask('start');start=time.monotonic();allSilent=True
 while time.monotonic()-start<5:
  time.sleep(.1);s=ask('status');assert s['running'];m=next(m for m in s['meters'] if m['id']=='out');allSilent&=m['peakLeft']==0 and m['peakRight']==0;assert allSilent,'Muted output must remain exactly zero'
 report['elapsedSeconds']=time.monotonic()-start;report['outputAlwaysSilent']=allSilent
 report['status']={key:s[key] for key in ['callbackCount','processedFrames','gateSkipCount','xruns','callbackDeadlineMisses','maxCallbackMs','deviceLatencyFrames','graphLatencyFrames','sampleRate','bufferFrames']}
 ask('stop');ask('shutdown');p.stdin.close();p.wait(timeout=10);report['passed']=p.returncode==0 and s['callbackCount']>500 and s['gateSkipCount']==0
except Exception as e:report['passed']=False;report['error']=str(e);raise
finally:
 if p.poll() is None:p.kill();p.wait()
 (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('Same-card real duplex muted audit',report['passed'])
