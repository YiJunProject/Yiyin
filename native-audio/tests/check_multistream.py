"""Focused real-engine multi-endpoint consumer; --hardware opens only named silent render endpoints."""
import argparse, hashlib, json, mmap, pathlib, queue, struct, subprocess, sys, threading, time, uuid
sys.stdout.reconfigure(encoding='utf-8')
root = pathlib.Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--evidence', required=True)
parser.add_argument('--hardware', action='store_true')
parser.add_argument('--seconds', type=float, default=15)
args = parser.parse_args()
destination = root / args.evidence; destination.mkdir(parents=True, exist_ok=True)
exe = root / 'native-audio/bin/Yideck.Audio.Engine.exe'
engine_hash = hashlib.sha256(exe.read_bytes()).hexdigest()
process = subprocess.Popen([str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, encoding='utf-8')
messages = queue.Queue(); errors = []
threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
threading.Thread(target=lambda: [errors.append(line.rstrip()) for line in process.stderr], daemon=True).start()
sequence = 0; checks = []; observations = []; failure = None

def request(op, **values):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps(dict(id=sequence, op=op, **values)) + '\n'); process.stdin.flush()
    response = json.loads(messages.get(timeout=20))
    assert response.get('id') == sequence, response
    return response

def good(op, **values):
    response = request(op, **values)
    if not response['ok']: raise RuntimeError(response)
    return response['result']

def check(name, condition, detail=None):
    checks.append(dict(name=name, passed=bool(condition), detail=detail))
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition: raise AssertionError(name)

def safe_status(status):
    copy = json.loads(json.dumps(status))
    clock = copy.pop('mediaClockOutputDeviceId', None)
    if clock is not None:
        copy['mediaClockOutputDeviceIdSha256'] = hashlib.sha256(clock.encode()).hexdigest()
    for port in copy.get('endpoints', []):
        port['deviceIdSha256'] = hashlib.sha256(port.pop('deviceId').encode()).hexdigest()
    return copy

try:
    ping = good('ping')
    check('Multi-endpoint capability without ASIO', ping['capabilities']['sameDeviceMultiEndpoint'] and not ping['capabilities']['asio'])
    devices = good('devices')['devices']
    main = next(d for d in devices if d['outputChannels'] >= 2 and 'minifuse' in d['name'].lower() and 'main' in d['name'].lower())
    auxiliary = next(d for d in devices if d['outputChannels'] >= 2 and '3/4' in d['name'] and d['groupId'] == main['groupId'])
    rate = main['preferredSampleRate']
    check('Selected actual same-card endpoints share the current Windows rate', main['groupId'] and main['groupId'] == auxiliary['groupId'] and rate == auxiliary['preferredSampleRate'], dict(main=main['name'], auxiliary=auxiliary['name'], rate=rate))
    config = dict(api='wasapi', inputEndpoints=[], outputEndpoints=[dict(deviceId=d['stableId'], channels=2) for d in (main, auxiliary)], clockOutputDeviceId=main['stableId'], sampleRate=rate, bufferFrames=256)
    check('No output endpoint rejected', not request('configure', **dict(config, outputEndpoints=[]))['ok'])
    check('Duplicate endpoint rejected', not request('configure', **dict(config, outputEndpoints=[config['outputEndpoints'][0]] * 2))['ok'])
    check('Actual endpoint channel bound enforced', not request('configure', **dict(config, outputEndpoints=[dict(deviceId=main['stableId'], channels=main['outputChannels']+1)]))['ok'])
    check('Rate mismatch cannot invoke implicit resampling', not request('configure', **dict(config, sampleRate=48000 if rate == 44100 else 44100))['ok'])
    check('Unselected primary output rejected', not request('configure', **dict(config, clockOutputDeviceId='missing-endpoint'))['ok'])
    configured = good('configure', **config)
    check('Output offsets describe two real stereo endpoints', configured['inputChannels'] == 0 and configured['outputChannels'] == 4 and [p['channelOffset'] for p in configured['endpoints']] == [0,2])
    fixture = root / 'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3'
    scanned = json.loads(subprocess.check_output([str(exe.with_name('Yideck.Audio.Scan.exe')), str(fixture)], encoding='utf-8', timeout=20))
    plugin = dict(instanceId='one-dsp-instance', format='vst3', path=str(fixture), classId=scanned['plugins'][0]['classId'])
    graph = dict(nodes=[dict(id='media', kind='media', channels=2, plugins=[plugin]),
                        dict(id='main', kind='output', channels=2, endpointId=main['stableId'], deviceChannels=[0,1]),
                        dict(id='aux', kind='output', channels=2, endpointId=auxiliary['stableId'], deviceChannels=[0,1])],
                 edges=[dict(id='main-route', **{'from':'media', 'to':'main', 'gain':1}),
                        dict(id='aux-route', **{'from':'media', 'to':'aux', 'gain':.25, 'source':'pre'})])
    prepared = good('setGraph', graph=graph)
    check('One prepared graph resolves both endpoint identities', prepared['hardwareReady'] and prepared['nodeCount'] == 3 and prepared['graphLatencyFrames'] == 17)
    invalid = json.loads(json.dumps(graph)); invalid['nodes'][2]['deviceChannels'] = [0,2]
    check('Graph cannot cross endpoint channel boundaries', not request('setGraph', graph=invalid)['ok'])
    name = 'Local\\YideckAudio-Multistream-' + uuid.uuid4().hex
    capacity = 65536
    with mmap.mmap(-1, 128 + capacity*8, tagname=name) as mapping:
        struct.pack_into('<6I', mapping, 0, 0x59415231, 1, capacity, 2, rate, 0)
        good('attachMedia', name=name); good('resetMedia', generation=1)
        struct.pack_into('<ff', mapping, 128, 1.0, .25)
        struct.pack_into('<Q', mapping, 24, 64)
        samples = good('offline', frames=64)['samples']
        check('Distinct named output routes retain stereo channels and gains', samples[68:72] == [.5,.125,.125,.03125] and all(v == 0 for v in samples[:68]))
        state = good('status')
        check('One media timeline is consumed once for two outputs', state['mediaConsumedFrames'] == 64 and state['processedFrames'] == 64)
        check('Read-only planning and offline graph opened no audio stream', not state['running'] and state['callbackCount'] == 0)
        good('setControls', nodes=[dict(id='media', gain=0)])
        struct.pack_into('<ff', mapping, 128 + 64*8, 1.0, .25)
        struct.pack_into('<Q', mapping, 24, 128)
        prefader = good('offline', frames=64)['samples']
        check('Pre-fader send bypasses the track fader gain', prefader[70:72] == [.125,.03125])
        good('setControls', nodes=[dict(id='media', mute=True)])
        struct.pack_into('<ff', mapping, 128 + 128*8, 1.0, .25)
        struct.pack_into('<Q', mapping, 24, 192)
        muted = good('offline', frames=64)['samples']
        check('Track mute blocks main and pre-fader sends including PDC tails', all(v == 0 for v in muted))
        good('setControls', nodes=[dict(id='media', gain=1, mute=False)])
        # Clear the impulse before any hardware starts. The remainder of this file
        # supplies digital zero only; it opens no capture endpoint and stores no PCM.
        mapping[128:] = bytes(capacity*8)
        good('resetMedia', generation=2)
        struct.pack_into('<Q', mapping, 24, 192 + capacity//2)
        for _ in range(2): good('offline', frames=256)
        if args.hardware:
            start = good('start')
            check('Both real WASAPI render endpoints start at the chosen rate', start['running'] and start['sampleRate'] == rate and len(start['endpoints']) == 2)
            begin = time.monotonic(); first = good('status'); first['elapsedSeconds'] = time.monotonic() - begin; observations.append(safe_status(first))
            while time.monotonic() - begin < args.seconds:
                read = struct.unpack_from('<Q', mapping, 32)[0]
                struct.pack_into('<Q', mapping, 24, read + capacity//2)
                time.sleep(.1)
                current = good('status'); current['elapsedSeconds'] = time.monotonic() - begin; observations.append(safe_status(current))
                if not current['running']: raise RuntimeError(current['error'] or 'Endpoint stopped')
            final = observations[-1]
            check('Single DSP clock and both endpoint callbacks advance', final['callbackCount'] > 100 and all(p['callbackCount'] > 100 for p in final['endpoints']))
            check('Steady endpoint bridges report no underflow or overflow', all(p['bridgeUnderflows'] == 0 and p['bridgeOverflows'] == 0 for p in final['endpoints']))
            check('Both actual output-node meters remain digitally silent', all(m['peakLeft'] == 0 and m['peakRight'] == 0 for m in final['meters']))
            check('No callback gating occurred during status polling', final['gateSkipCount'] == 0)
            check('Shared media is processed once per primary DSP callback', final['processedFrames'] == final['callbackCount'] * 256 + 704)
            good('stop'); stopped = good('status')
            counts = [p['callbackCount'] for p in stopped['endpoints']]
            time.sleep(.15)
            check('Stop quiesces every owned endpoint', not stopped['running'] and [p['callbackCount'] for p in good('status')['endpoints']] == counts)
        good('detachMedia', generation=2)
    good('clearGraph')
    missing = dict(nodes=[dict(id='offline-editor', kind='output', channels=2, endpointId='unavailable-card', deviceChannels=[0,1], plugins=[plugin])], edges=[])
    result = good('setGraph', graph=missing)
    check('Stopped plugin preparation works without the original sound card', not result['hardwareReady'] and good('pluginState', instanceId='one-dsp-instance'))
    check('Unresolved hardware graph cannot start silently', not request('start')['ok'])
    good('shutdown'); process.stdin.close(); process.wait(timeout=8)
    check('Task-owned native engine exits cleanly', process.returncode == 0)
except Exception as error:
    failure = repr(error)
    print(failure, file=sys.stderr)
finally:
    if process.poll() is None:
        try: request('shutdown'); process.wait(timeout=5)
        except Exception: process.kill(); process.wait()
    stable = [s for s in observations if s.get('elapsedSeconds', 0) >= 5]
    drift = {}
    if stable:
        for index in range(len(stable[-1]['endpoints'])):
            ports = [s['endpoints'][index] for s in stable]
            differences = [s['endpoints'][index]['frames'] - next(p['frames'] for p in s['endpoints'] if p['clock']) for s in stable]
            drift[ports[-1]['deviceIdSha256']] = dict(clock=ports[-1]['clock'],
                minimumBridgeQueuedFrames=min(p['bridgeQueuedFrames'] for p in ports),
                maximumBridgeQueuedFrames=max(p['bridgeQueuedFrames'] for p in ports),
                minimumDeviceQueueFrames=min(p['deviceQueueFrames'] for p in ports),
                maximumDeviceQueueFrames=max(p['deviceQueueFrames'] for p in ports),
                frameOffsetToPrimaryMinimum=min(differences), frameOffsetToPrimaryMaximum=max(differences),
                frameOffsetNetChange=differences[-1]-differences[0],
                bridgeUnderflows=ports[-1]['bridgeUnderflows'], bridgeOverflows=ports[-1]['bridgeOverflows'])
    report = dict(passed=failure is None, checks=checks, count=len(checks), error=failure,
                  engineSha256=engine_hash, hardware=args.hardware, seconds=args.seconds if args.hardware else 0,
                  scope='Real native graph, official SDK VST3 fixture, shared PCM ring; actual render endpoints only when hardware=true; digital silence; no capture, ASIO, resampling, physical loopback or production process changes',
                  observations=observations, afterFiveSecondWarmup=drift, stderr=errors)
    (destination/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
sys.exit(1 if failure else 0)
