"""Read-only native IPC + Windows endpoint/ASIO metadata. Never initializes/starts an audio stream."""
import argparse,pathlib,subprocess,json,hashlib,sys,winreg
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser();parser.add_argument('--exe',default=str(root/'native-audio/bin/Yideck.Audio.Engine.exe'));parser.add_argument('--output',required=True);parser.add_argument('--verify-metadata',action='store_true');parser.add_argument('--baseline');args=parser.parse_args()
exe=pathlib.Path(args.exe).resolve();out=pathlib.Path(args.output).resolve();out.parent.mkdir(parents=True,exist_ok=True)
if out.exists():raise RuntimeError('Preserve existing evidence; use a new report path')
if not out.is_relative_to(root/'evidence'):raise RuntimeError('Evidence must remain in project/evidence')
p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8');seq=0
def ask(op):
 global seq
 seq+=1;p.stdin.write(json.dumps({'id':seq,'op':op})+'\n');p.stdin.flush();r=json.loads(p.stdout.readline())
 if not r['ok']:raise RuntimeError(str(r))
 return r['result']
def hashed(s):return hashlib.sha256(s.lower().encode('utf-8')).hexdigest() if s else None
properties=json.loads(subprocess.check_output([str(root/'native-audio/test-bin/Yideck.Test.DeviceProperties.exe')],encoding='utf-8',timeout=10))['devices']
def windows_properties(device):
 flow='capture' if device['inputChannels'] else 'render'
 matches=[x for x in properties if x['name']==device['name'] and x['flow']==flow]
 if len(matches)!=1:raise RuntimeError('Read-only comparison requires an unambiguous endpoint name and flow on this test machine')
 return matches[0]
def asio_names(view):
 result=[]
 try:
  with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SOFTWARE\ASIO',0,winreg.KEY_READ|view) as k:
   for i in range(winreg.QueryInfoKey(k)[0]):result.append(winreg.EnumKey(k,i))
 except FileNotFoundError:pass
 return sorted(result)
try:
 ping=ask('ping');before=ask('status');raw=ask('devices')['devices'];after=ask('status')
 assert not before['running'] and not after['running'] and after['callbackCount']==0
 devices=[]
 for d in raw:
  clean={k:v for k,v in d.items() if k not in ['id','stableId','groupId','runtimeId']}
  clean.update(endpointIdSha256=hashed(d['id']),groupIdSha256=hashed(d.get('groupId')),windows=windows_properties(d));devices.append(clean)
 groups={}
 for d in devices:groups.setdefault(d['groupIdSha256'],[]).append(d['name'])
 ask('shutdown');p.stdin.close();p.wait(timeout=10)
 assert p.returncode==0
 checks=[]
 def check(name,value):checks.append({'name':name,'passed':bool(value)});assert value,name
 check('Enumeration does not start device callbacks',not after['running'] and after['callbackCount']==0)
 check('WASAPI supported and ASIO absent',ping['capabilities']['wasapi'] and not ping['capabilities']['asio'])
 if args.verify_metadata:
  check('Native adapter names match independent IMMDevice property store',all(d.get('groupName')==d['windows']['groupName'] for d in devices))
  check('Native manufacturer strings match optional Windows metadata',all(d.get('manufacturer')==d['windows']['manufacturer'] for d in devices))
  check('Advertised rates match actual supported Windows mix rate',all(d['sampleRates']==([d['preferredSampleRate']] if d['preferredSampleRate'] in (44100,48000) else []) for d in devices))
  check('Observed MiniFuse endpoints share one real Windows container',len([d for d in devices if d['groupName']=='Minifuse 1'])==8 and len({d['groupIdSha256'] for d in devices if d['groupName']=='Minifuse 1'})==1)
 if args.baseline:
  baseline=json.loads(pathlib.Path(args.baseline).read_text(encoding='utf-8'))
  keys=['endpointIdSha256','groupIdSha256','name','inputChannels','outputChannels','preferredSampleRate','api']
  view=lambda rows:sorted([{k:d[k] for k in keys} for d in rows],key=lambda d:d['endpointIdSha256'])
  check('Existing endpoint identity group channels and mix format unchanged',view(devices)==view(baseline['devices']))
 report={'engineSha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'scope':'Current Windows active endpoint metadata + native devices IPC only; no configure, graph, initialize/start stream, plugin, production data or driver-setting changes. Endpoint/container identities are hashed. Independent IMMDevice property-store tool is read-only. ASIO registry names prove registration only, not availability/compatibility or host support.','capabilities':ping['capabilities'],'devices':devices,'groups':groups,'registeredAsio':{'64-bit':asio_names(winreg.KEY_WOW64_64KEY),'32-bit':asio_names(winreg.KEY_WOW64_32KEY)},'callbacksBefore':before['callbackCount'],'callbacksAfter':after['callbackCount'],'runningAfter':after['running'],'ownedProcessExitCode':p.returncode,'checks':checks,'passed':sum(c['passed'] for c in checks),'total':len(checks)}
 out.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
 print(out);print('Active endpoints:',len(devices),'physical Windows groups:',len(groups),'callbacks:',after['callbackCount'])
finally:
 if p.poll() is None:p.kill();p.wait()
