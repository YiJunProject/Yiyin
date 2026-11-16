"""Production JSONL transaction/PDC consumer. No configure/devices/start calls."""
import argparse,base64,hashlib,json,pathlib,queue,struct,subprocess,sys,threading
sys.stdout.reconfigure(encoding='utf-8')
ap=argparse.ArgumentParser();ap.add_argument('--engine',type=pathlib.Path,required=True);ap.add_argument('--fixture',type=pathlib.Path,required=True);ap.add_argument('--output',type=pathlib.Path,required=True);a=ap.parse_args()
if a.output.exists():raise RuntimeError('Keep prior evidence; choose a new report')
a.output.parent.mkdir(parents=True,exist_ok=True)
p=subprocess.Popen([str(a.engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
responses=queue.Queue();threading.Thread(target=lambda:[responses.put(x) for x in p.stdout],daemon=True).start();seq=0;checks=[]
def request(op,**fields):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**fields))+'\n');p.stdin.flush();return json.loads(responses.get(timeout=15))
def good(op,**fields):
 r=request(op,**fields)
 if not r['ok']:raise RuntimeError(r)
 return r['result']
def check(name,value):
 checks.append(dict(name=name,passed=bool(value)))
 if not value:raise AssertionError(name)
def run():return good('offline',frames=64,input=[1.]*128)['samples']
report=dict(engine=str(a.engine.resolve()),engineSha256=hashlib.sha256(a.engine.read_bytes()).hexdigest(),fixtureSha256=hashlib.sha256(a.fixture.read_bytes()).hexdigest(),hardwareOpened=False,checks=checks)
try:
 ping=good('ping');check('final product native version and ASIO build metadata',ping['version']=='1.2.0' and ping['capabilities']['asio'])
 plug=dict(instanceId='fx',format='vst2',path=str(a.fixture.resolve()),wet=.5)
 graph=dict(nodes=[dict(id='in',kind='input'),dict(id='fx',kind='fx',plugins=[plug]),dict(id='out',kind='output')],edges=[dict(id='in-fx',**{'from':'in','to':'fx'}),dict(id='fx-out',**{'from':'fx','to':'out'}),dict(id='dry',**{'from':'in','to':'out'})])
 good('setGraph',graph=graph);run();check('real VST2 parallel wet/dry path reaches expected samples',all(abs(v-1.75)<1e-7 for v in run()))
 nextGraph=json.loads(json.dumps(graph));nextGraph['nodes'].append(dict(id='unused',kind='bus'))
 token=good('beginGraphEdit',graph=nextGraph)['transaction']
 check('prepare leaves original graph active',len(good('status')['meters'])==3)
 check('old waveform continues while persistence is pending',all(abs(v-1.75)<1e-7 for v in run()))
 check('nested graph replacement is rejected',not request('setGraph',graph=graph)['ok'])
 good('commitGraphEdit',transaction=token)
 check('unrelated commit preserves actual PDC samples including first frame',all(abs(v-1.75)<1e-7 for v in run()))
 check('committed candidate becomes visible',len(good('status')['meters'])==4)
 check('stale rollback cannot undo committed graph',not request('rollbackGraphEdit',transaction=token)['ok'])
 good('setParameter',instanceId='fx',parameterId=0,value=.625);state=good('pluginState',instanceId='fx')
 run();check('native parameter is captured current',abs(struct.unpack('<f',base64.b64decode(state['chunk']))[0]-.625)<1e-7)
 removal=json.loads(json.dumps(nextGraph));removal['nodes'][1]['plugins']=[]
 token=good('beginGraphEdit',graph=removal)['transaction']
 check('removed instance remains available until commit',good('pluginState',instanceId='fx')==state)
 good('rollbackGraphEdit',transaction=token)
 check('rollback retains current instance state',good('pluginState',instanceId='fx')==state)
 check('rollback does not reset live dry or route delay',all(abs(v-1.8125)<1e-7 for v in run()))
 token=good('beginGraphEdit',graph=nextGraph)['transaction']
 good('commitGraphEdit',transaction=token,nodes=[dict(id='out',gain=.25)])
 changed=run();check('commit applies latest controls on first newly active block',abs(changed[-1]-.453125)<1e-6)
 check('new controls remain effective',all(abs(v-.453125)<1e-6 for v in run()))
 bad=json.loads(json.dumps(nextGraph));bad['edges'].append(dict(id='cycle',**{'from':'out','to':'in'}))
 check('invalid candidate rejects without changing running structure',not request('beginGraphEdit',graph=bad)['ok'])
 check('failed prepare preserves current output',all(abs(v-.453125)<1e-6 for v in run()))
 check('offline audit has not opened a device',good('status')['callbackCount']==0 and not good('status')['running'])
 report['passed']=True
except Exception as error:report.update(passed=False,error=str(error))
finally:
 try:good('shutdown')
 except Exception:pass
 try:p.wait(timeout=5)
 except subprocess.TimeoutExpired:p.kill();p.wait()
 report['count']=len(checks);a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(passed=report['passed'],count=len(checks),output=str(a.output)),ensure_ascii=False))
raise SystemExit(0 if report['passed'] else 1)
