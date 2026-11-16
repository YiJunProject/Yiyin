"""Explicitly authorized MiniFuse-only 5-minute silent hardware test, 60 Hz meters and 5 Hz status."""
import argparse,ctypes as c,ctypes.wintypes as w,hashlib,json,math,os,pathlib,queue,statistics,subprocess,sys,threading,time,traceback
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
ap=argparse.ArgumentParser();ap.add_argument('--seconds',type=float,default=300);ap.add_argument('--engine',type=pathlib.Path,default=root/'native-audio/bin-v0150/Yideck.Audio.Engine.exe');ap.add_argument('--output',type=pathlib.Path,default=root/'evidence/v0150-native/hardware-five-minute-current');a=ap.parse_args();a.output.mkdir(parents=True,exist_ok=True)
driver='asio:{9B2A3761-A0F5-4544-96AE-E45FB7B6E982}'
class Mem(c.Structure):_fields_=[('cb',w.DWORD),('PageFaultCount',w.DWORD),('PeakWorkingSetSize',c.c_size_t),('WorkingSetSize',c.c_size_t),('QuotaPeakPagedPoolUsage',c.c_size_t),('QuotaPagedPoolUsage',c.c_size_t),('QuotaPeakNonPagedPoolUsage',c.c_size_t),('QuotaNonPagedPoolUsage',c.c_size_t),('PagefileUsage',c.c_size_t),('PeakPagefileUsage',c.c_size_t),('PrivateUsage',c.c_size_t)]
k=c.windll.kernel32;ps=c.windll.psapi;k.GetProcessTimes.argtypes=[w.HANDLE,c.POINTER(w.FILETIME),c.POINTER(w.FILETIME),c.POINTER(w.FILETIME),c.POINTER(w.FILETIME)];ps.GetProcessMemoryInfo.argtypes=[w.HANDLE,c.POINTER(Mem),w.DWORD]
class Client:
 def __init__(self,label):
  self.log=(a.output/(label+'-stderr.log')).open('w',encoding='utf-8');self.p=subprocess.Popen([str(a.engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,text=True,encoding='utf-8',bufsize=1);self.q=queue.Queue();self.id=0
  threading.Thread(target=lambda:[self.q.put(x) for x in self.p.stdout],daemon=True).start()
 def call(self,op,**kw):
  self.id+=1;self.p.stdin.write(json.dumps(dict(id=self.id,op=op,**kw))+'\n');self.p.stdin.flush();raw=self.q.get(timeout=20);r=json.loads(raw)
  if not r['ok']:raise RuntimeError(r)
  return r['result'],len(raw.encode('utf-8'))
 def ask(self,op,**kw):return self.call(op,**kw)[0]
 def usage(self):
  times=[w.FILETIME() for _ in range(4)];k.GetProcessTimes(w.HANDLE(int(self.p._handle)),*[c.byref(x) for x in times]);cpu=sum((x.dwHighDateTime<<32)+x.dwLowDateTime for x in times[2:])/1e7
  m=Mem();m.cb=c.sizeof(m);ps.GetProcessMemoryInfo(w.HANDLE(int(self.p._handle)),c.byref(m),c.sizeof(m));return dict(cpuSeconds=cpu,workingSetBytes=m.WorkingSetSize,peakWorkingSetBytes=m.PeakWorkingSetSize,privateBytes=m.PrivateUsage)
 def close(self):
  if self.p.poll() is None:
   try:self.ask('shutdown');self.p.stdin.close();self.p.wait(10)
   except Exception:self.p.kill();self.p.wait()
  self.log.close()
checks=[]
def check(ok,name):
 checks.append(dict(name=name,passed=bool(ok)))
 if not ok:raise AssertionError(name)
def probe(label):
 p=Client(label)
 try:return p.ask('probeDevice',api='asio',deviceId=driver)['device']
 finally:p.close()
def percentile(values,q):return sorted(values)[min(len(values)-1,int(len(values)*q))] if values else 0
report=dict(engine=str(a.engine),engineSha256=hashlib.sha256(a.engine.read_bytes()).hexdigest(),hardwareOpened=True,audioStreamStarted=False,inputPcmRetained=False,signal='Exact digital zero; all physical inputs monitor=false, all outputs muted from prepared first block',requestedMeterHz=60,requestedStatusHz=5,requestedSeconds=a.seconds)
p=None
try:
 before=probe('before-probe');report['beforeProbe']=before
 check(before['currentSampleRate']==48000,'MiniFuse currently uses 48000 Hz; test does not change rate')
 block=before['bufferPreferredFrames'];check(block in (128,256),'Use current reported preferred buffer, not claimed current client buffer');report['actualRequestedBufferFrames']=block
 p=Client('engine');check(p.ask('ping')['capabilities']['intervalMeters'],'interval meters advertised')
 p.ask('configure',api='asio',sampleRate=48000,bufferFrames=block,inputEndpoints=[dict(deviceId=driver,channels=8)],outputEndpoints=[dict(deviceId=driver,channels=8)],clockOutputDeviceId=driver)
 fixture=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
 scan=json.loads(subprocess.check_output([str(a.engine.with_name('Yideck.Audio.Scan.exe')),str(fixture)],encoding='utf-8',timeout=15));check(scan['ok'],'real compiled VST3 fixture scans')
 nodes=[dict(id='input-disabled',kind='input',channels=2,deviceChannels=[0,1],endpointId=driver,mute=True,monitor=False)]
 nodes += [dict(id='bus'+str(i),kind='bus',channels=2) for i in range(27)]
 nodes[1]['plugins']=[dict(instanceId='pdc-fixture',format='vst3',path=str(fixture),classId=scan['plugins'][0]['classId'])]
 nodes += [dict(id='out'+str(i),kind='output',channels=2,deviceChannels=[i,i+1],endpointId=driver,mute=True) for i in range(0,8,2)]
 edges=[dict(id='bus'+str(i)+':main',**{'from':'bus'+str(i),'to':'bus'+str(i+1)}) for i in range(26)]
 edges += [dict(id='out-route'+str(i),**{'from':'bus26','to':'out'+str(i)}) for i in range(0,8,2)]
 p.ask('setGraph',graph=dict(nodes=nodes,edges=edges));report['graph']=dict(nodeCount=len(nodes),edgeCount=len(edges),realVst3Count=1,physicalInputRoutes=0,outputChannels=8,inputChannels=8)
 check(all(x==0 for x in p.ask('offline',frames=block,input=[0.0]*(8*block))['samples']),'prepared graph outputs exact zero')
 p.ask('meters');baseline=p.ask('status')['processedFrames'];usage0=p.usage()
 started=p.ask('start');report['audioStreamStarted']=True;t0=time.perf_counter();nextmeter=t0;nextstatus=t0;nextprogress=t0+5
 times=[];rpc=[];status_times=[];status_samples=[];memory=[];totals={n['id']:0 for n in nodes};mincount=10**9;maxcount=0;empty=0;bytesout=0;missed=0
 def consume(result):
  global mincount,maxcount,empty
  if len(result['meters'])!=len(nodes):raise RuntimeError('Meter node count changed')
  for m in result['meters']:
   if any(m[x]!=0 for x in ['peakLeft','peakRight','rmsLeft','rmsRight']) or m['clipped']:raise RuntimeError('Silent hardware graph reports nonzero meter')
   count=m['sampleCount'];totals[m['id']]+=count;mincount=min(mincount,count);maxcount=max(maxcount,count);empty+=count==0
   if count%block:raise RuntimeError('Interval count is not an actual whole callback block')
 while time.perf_counter()-t0<a.seconds:
  now=time.perf_counter()
  if now<nextmeter:time.sleep(nextmeter-now)
  begin=time.perf_counter();m,size=p.call('meters');finish=time.perf_counter();times.append(begin);rpc.append((finish-begin)*1000);bytesout+=size;consume(m);nextmeter+=1/60
  if finish-nextmeter>1/60:missed+=int((finish-nextmeter)*60);nextmeter=finish+1/60
  if finish>=nextstatus:
   s=p.ask('status');status_times.append(time.perf_counter());status_samples.append(s);memory.append(p.usage());nextstatus=finish+.2
   if not s['running'] or s['restartRequired'] or s['message']:raise RuntimeError('Hardware runtime fault: '+json.dumps(s))
  if finish>=nextprogress:
   (a.output/'progress.json').write_text(json.dumps(dict(elapsedSeconds=finish-t0,meterPolls=len(times),statusPolls=len(status_times),callbackCount=status_samples[-1]['callbackCount'],passedSoFar=True)),encoding='utf-8');nextprogress=finish+5
 elapsed=time.perf_counter()-t0;p.ask('stop');consume(p.ask('meters'));final=p.ask('status');usage1=p.usage();report.update(elapsedSeconds=elapsed,finalStatus=final)
 expected=final['processedFrames']-baseline
 check(expected>0 and final['callbackCount']>0,'real hardware callback frames occurred')
 check(all(x==expected for x in totals.values()),'every node interval sum exactly accounts for every hardware-processed frame')
 check(final['gateSkipCount']==0,'60 Hz meter and 5 Hz status polling do not gate audio')
 check(final['xruns']==0 and final['callbackDeadlineMisses']==0,'no backend xrun or callback deadline miss observed')
 check(final['asio']['samplePositionDiscontinuities']==0,'driver sample clock has no observed discontinuity')
 check(not final['restartRequired'] and not final['driverResetRequired'],'no processor/driver restart required')
 check(final['graphLatencyFrames']==17,'real VST3 graph has 17-frame PDC')
 check(final['outputLatencyFrames']==final['deviceLatencyFrames']+17,'media clock includes driver output delay plus graph PDC')
 check(final['bridgeBufferFrames']==0,'ASIO path uses no bridge')
 gaps=[(b-a)*1000 for a,b in zip(times,times[1:])]
 report['meterPolling']=dict(polls=len(times),actualHz=(len(times)-1)/(times[-1]-times[0]),missedScheduleSlots=missed,rpcMeanMs=statistics.mean(rpc),rpcP95Ms=percentile(rpc,.95),rpcMaxMs=max(rpc),pollGapMaxMs=max(gaps),pollGapP95Ms=percentile(gaps,.95),responseBytes=bytesout,intervalSampleCountMin=mincount,intervalSampleCountMax=maxcount,emptyNodeIntervals=empty,totalSamplesPerNode=totals,expectedSamplesPerNode=expected)
 report['statusPolling']=dict(polls=len(status_times),actualHz=(len(status_times)-1)/(status_times[-1]-status_times[0]))
 cpu=usage1['cpuSeconds']-usage0['cpuSeconds'];report['processUsage']=dict(logicalProcessorCount=os.cpu_count(),cpuSeconds=cpu,cpuOneCorePercent=100*cpu/elapsed,cpuWholeMachinePercent=100*cpu/elapsed/os.cpu_count(),peakWorkingSetMiB=max(x['peakWorkingSetBytes'] for x in memory+[usage1])/1048576,maxObservedPrivateMiB=max(x['privateBytes'] for x in memory+[usage1])/1048576,scope='Owned engine process only; polling harness excluded')
 report['statusSamples']=status_samples
 after=probe('after-stop-probe');report['afterProbe']=after;check(after['currentSampleRate']==before['currentSampleRate'] and after['bufferPreferredFrames']==before['bufferPreferredFrames'],'stop releases MiniFuse for isolated reprobe; rate and preferred buffer unchanged')
 p.close();check(p.p.returncode==0,'owned engine exits cleanly');p=None;report['passed']=True
except Exception as e:report.update(passed=False,error=str(e),traceback=traceback.format_exc())
finally:
 if p:p.close()
 report.update(checks=checks,checkCount=len(checks));(a.output/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['beforeProbe','afterProbe','finalStatus','checks','statusSamples']},ensure_ascii=False));sys.exit(0 if report.get('passed') else 1)
