"""Focused ASIO consumer. Only --hardware opens MiniFuse, always digital silence."""
import argparse,hashlib,json,pathlib,queue,subprocess,sys,threading,time,traceback

sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2]
ap=argparse.ArgumentParser()
ap.add_argument('--engine',type=pathlib.Path,default=root/'native-audio/bin-asio-dev/Yideck.Audio.Engine.exe')
ap.add_argument('--output',type=pathlib.Path,required=True)
ap.add_argument('--hardware',action='store_true')
ap.add_argument('--seconds',type=float,default=300)
ap.add_argument('--fixture',action='store_true',help='Use the real compiled 17-frame VST3 fixture on a silent output')
a=ap.parse_args();a.output.mkdir(parents=True,exist_ok=True)
driver='asio:{9B2A3761-A0F5-4544-96AE-E45FB7B6E982}'
checks=[];records=[]
def check(ok,name,**extra):
    checks.append(dict(name=name,passed=bool(ok),**extra))
    if not ok:raise AssertionError(name)
class Client:
    def __init__(self,label):
        self.log=(a.output/(label+'-stderr.log')).open('w',encoding='utf-8')
        self.p=subprocess.Popen([str(a.engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,text=True,encoding='utf-8',bufsize=1)
        self.q=queue.Queue();self.id=0
        threading.Thread(target=self.reader,daemon=True).start()
    def reader(self):
        for line in self.p.stdout:self.q.put(line)
        self.q.put(None)
    def call(self,op,**kw):
        self.id+=1;request=dict(id=self.id,op=op,**kw)
        self.p.stdin.write(json.dumps(request)+'\n');self.p.stdin.flush()
        response=self.q.get(timeout=30)
        if response is None:raise RuntimeError('Native engine exited unexpectedly')
        r=json.loads(response);records.append(dict(request=request,response=r))
        if not r['ok']:raise RuntimeError(r.get('error'))
        return r['result']
    def close(self):
        if self.p.poll() is None:
            try:self.call('shutdown')
            except Exception:pass
            self.p.stdin.close()
            try:self.p.wait(10)
            except subprocess.TimeoutExpired:self.p.kill();self.p.wait()
        self.log.close()
        return self.p.returncode
def isolated_probe(label):
    c=Client(label)
    try:return c.call('probeDevice',api='asio',deviceId=driver)['device']
    finally:c.close()
c=None;report={'engine':str(a.engine),'engineSha256':hashlib.sha256(a.engine.read_bytes()).hexdigest(),
    'hardwareOpened':a.hardware,'inputPcmRetained':False,'signal':'digital zero; input monitoring disabled',
    'preferredBufferIsNotClaimedCurrent':True}
try:
    c=Client('engine')
    check(c.call('ping')['capabilities']['asio'],'ASIO capability enabled')
    initial=c.call('status');check(not initial['running'] and initial['callbackCount']==0,'initial status safe without configure')
    ds=c.call('devices',api='asio')['devices'];check(any(d['stableId']==driver for d in ds),'MiniFuse x64 registry entry available')
    check(all(d['probeRequired'] for d in ds),'registry enumeration does not probe drivers')
    if a.hardware:
        before=isolated_probe('before-probe');report['beforeProbe']=before
        sr=before['currentSampleRate'];bs=before['bufferPreferredFrames']
        check(sr in (44100,48000),'current driver sample rate supported')
        ni=before['inputChannels'];no=before['outputChannels']
        config=dict(api='asio',sampleRate=sr,bufferFrames=bs,inputEndpoints=[dict(deviceId=driver,channels=ni)],
            outputEndpoints=[dict(deviceId=driver,channels=no)],clockOutputDeviceId=driver)
        configured=c.call('configure',**config)
        check(configured['inputChannels']==ni and configured['outputChannels']==no,'single-driver duplex channels configured')
        concurrent=isolated_probe('after-configure-probe');check(concurrent['currentSampleRate']==sr,'configure releases driver for isolated probe')
        # There is no route from physical inputs to outputs. Every output is muted
        # from its first prepared block; each physical channel is allocated.
        nodes=[]
        for channel in range(0,no,2):
            channels=min(2,no-channel)
            nodes.append(dict(id='out'+str(channel),kind='output',channels=channels,deviceChannels=list(range(channel,channel+channels)),endpointId=driver,mute=True,plugins=[]))
        if a.fixture:
            fixture=root/'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
            scan=json.loads(subprocess.check_output([str(a.engine.with_name('Yideck.Audio.Scan.exe')),str(fixture)],encoding='utf-8',timeout=15))
            check(scan['ok'] and len(scan['plugins'])==1,'compiled VST3 fixture scanned')
            nodes[0]['plugins']=[dict(instanceId='latency-fixture',format='vst3',path=str(fixture),classId=scan['plugins'][0]['classId'])]
        if ni:
            nodes.append(dict(id='input-muted',kind='input',channels=min(2,ni),deviceChannels=list(range(min(2,ni))),endpointId=driver,monitor=False,mute=True,plugins=[]))
        c.call('setGraph',graph=dict(nodes=nodes,edges=[]))
        offline=c.call('offline',frames=bs,input=[0.0]*(bs*ni))
        check(all(x==0 for x in offline['samples']),'prepared graph renders exact zero before start')
        start=c.call('start');report['start']=start
        check(start['bridgeBufferFrames']==0,'ASIO uses no WASAPI bridge')
        check(start['sampleRate']==sr and start['bufferFrames']==bs,'start uses probed rate and preferred buffer without fallback')
        samples=[];t=time.monotonic()
        while time.monotonic()-t<a.seconds:
            time.sleep(.25)
            s=c.call('status');samples.append(s)
            if not s['running'] or s['restartRequired']:raise RuntimeError('ASIO stream stopped: '+s['message'])
            if any(m['peakLeft'] or m['peakRight'] for m in s['meters']):raise RuntimeError('Silent hardware graph has nonzero meter')
        elapsed=time.monotonic()-t;report['elapsedSeconds']=elapsed;report['samples']=samples
        final=samples[-1];report['finalStatus']=final
        check(final['asio']['samplePositionValid'],'driver sample clock delivered')
        check(final['asio']['samplePosition']>samples[0]['asio']['samplePosition'],'driver sample clock advances')
        check(final['callbackCount']>0,'actual ASIO processing callbacks occurred')
        check(final['gateSkipCount']==0,'no graph gate skips')
        check(not final['driverResetRequired'],'no driver reset or format change')
        check(final['bridgeBufferFrames']==0,'no bridge throughout hardware run')
        if a.fixture:
            check(final['graphLatencyFrames']==17,'real plugin graph reports 17-frame PDC')
            check(final['outputLatencyFrames']==final['asio']['outputLatencyFrames']+17,'media clock uses driver output latency plus graph PDC only')
        report['callbackObservations']={k:final.get(k) for k in ['callbackCount','callbackCpuPercent','maxCallbackMs','xruns','callbackDeadlineMisses']}
        report['driverClockDiscontinuities']=final['asio']['samplePositionDiscontinuities']
        report['outputLatencyRangeFrames']=[min(s['outputLatencyFrames'] for s in samples),max(s['outputLatencyFrames'] for s in samples)]
        c.call('stop');check(not c.call('status')['running'],'stop reports stopped')
        after=isolated_probe('after-stop-probe');report['afterProbe']=after
        check(after['currentSampleRate']==sr,'rate unchanged and driver released after stop')
        check(after['bufferPreferredFrames']==bs,'driver preferred buffer unchanged after stop')
    report['exitCode']=c.close();c=None;check(report['exitCode']==0,'isolated engine exits cleanly')
    report['passed']=True
except Exception as e:
    report['passed']=False;report['error']=str(e);report['traceback']=traceback.format_exc()
finally:
    if c:report['exitCode']=c.close()
    report['checks']=checks;report['checkCount']=len(checks)
    (a.output/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    # Offline known-zero arrays are omitted from protocol evidence, not captured PCM.
    for r in records:
        if r['request']['op']=='offline':r['request'].pop('input',None);r['response'].get('result',{}).pop('samples',None)
    (a.output/'protocol.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['samples','checks','beforeProbe','afterProbe','finalStatus']},ensure_ascii=False))
sys.exit(0 if report.get('passed') else 1)
