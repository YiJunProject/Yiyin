"""Read-only endpoint enumeration and configuration validation; never starts a stream."""
import pathlib,subprocess,json,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];exe=root/'native-audio/bin/Yideck.Audio.Engine.exe';checks=[]
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
def ask(op,**kw):
 p.stdin.write(json.dumps(dict(id=1,op=op,**kw))+'\n');p.stdin.flush();return json.loads(p.stdout.readline())
def check(name,value):checks.append({'name':name,'passed':bool(value)});assert value,name
try:
 ds=ask('devices')['result']['devices'];own=[d for d in ds if 'minifuse' in d['name'].lower()]
 check('Observed MiniFuse endpoints expose actual Windows container',len(own)>=2 and all(d.get('groupId') for d in own))
 check('Observed MiniFuse input and output containers match',len({d['groupId'] for d in own})==1)
 i=next(d for d in own if d['inputChannels']>0 and 'Input' in d['name']);o=next(d for d in own if d['outputChannels']>0 and 'Main' in d['name'])
 cfg={'inputDeviceId':i['id'],'outputDeviceId':o['id'],'inputChannels':1,'outputChannels':2,'sampleRate':44100,'bufferFrames':256}
 check('Same physical device configure succeeds without opening stream',ask('configure',**cfg)['ok'])
 check('Unresolvable capture endpoint rejected without fallback',not ask('configure',**dict(cfg,inputDeviceId='missing-task-owned-endpoint'))['ok'])
 check('Device never started during audit',not ask('status')['result']['running'])
 ask('shutdown');p.stdin.close();p.wait(timeout=10)
finally:
 if p.poll() is None:p.kill();p.wait()
 report={'checks':checks,'passed':sum(x['passed'] for x in checks),'total':len(checks),'engineSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'scope':'Actual Windows endpoint metadata/configure only; no stream started. Different/missing group rejection covered separately by production policy fixture.'}
 (root/'evidence/v0130-native-audio/device-groups-native-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(len(checks),'device-group checks passed')
