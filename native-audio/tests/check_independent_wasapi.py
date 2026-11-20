"""Run digital silence only on two explicitly named shared WASAPI render endpoints."""
import argparse
import hashlib
import json
import pathlib
import queue
import subprocess
import threading
import time

def run(args, report):
    checks = report["checks"]
    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})
        if not passed:
            raise AssertionError(name)

    messages = queue.Queue()
    engine = subprocess.Popen([args.engine], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              text=True, encoding="utf-8", stderr=subprocess.DEVNULL)
    threading.Thread(target=lambda: [messages.put(line) for line in engine.stdout], daemon=True).start()
    request_id = 0
    def call(op, *, expect_error=False, **values):
        nonlocal request_id
        request_id += 1
        engine.stdin.write(json.dumps(dict(id=request_id, op=op, **values)) + "\n")
        engine.stdin.flush()
        answer = json.loads(messages.get(timeout=12))
        if answer["id"] != request_id or answer["ok"] == expect_error:
            raise RuntimeError((op, answer))
        return answer.get("result", answer.get("error"))

    try:
        ping = call("ping")
        check("1.2.0 binary advertises independent WASAPI and hybrid ASIO", ping["version"] == "1.2.0" and
              all(ping["capabilities"][k] for k in ["asio", "independentWasapiEndpoints", "asioWasapiHybrid"]))
        devices = call("devices")["devices"]
        main, secondary = [next(d for d in devices if d["outputChannels"] >= 2 and d["name"] == name)
                           for name in (args.clock_name, args.aux_name)]
        report["wasapi"] = [dict(name=d["name"], rate=d["preferredSampleRate"]) for d in (main, secondary)]
        report["registeredAsioDrivers"] = [d["name"] for d in call("devices", api="asio")["devices"]]
        check("distinct physical containers selected", main["groupId"] and secondary["groupId"] and main["groupId"] != secondary["groupId"])
        missing = "unplugged-test-output"
        streams = [dict(deviceId=key, channels=2) for key in [secondary["id"], main["id"], missing]]
        cfg = dict(api="wasapi", ioMode="multi", inputEndpoints=[], outputEndpoints=streams,
                   clockOutputDeviceId=main["id"], sampleRate=main["preferredSampleRate"], bufferFrames=256)
        msg = call("configure", expect_error=True, **dict(cfg, ioMode="single", outputEndpoints=streams[:2]))
        check("legacy single mode still rejects different hardware", "different physical" in msg)
        call("configure", expect_error=True, **dict(cfg, ioMode="hybrid"))
        call("configure", expect_error=True, **dict(cfg, clockOutputDeviceId=missing))
        check("mode mismatch and missing clock rejected", True)
        configured = call("configure", **cfg)
        check("retained offsets include missing auxiliary and non-first clock",
              configured["ioMode"] == "multi" and [d["channelOffset"] for d in configured["endpoints"]] == [0,2,4])
        nodes = [dict(id=str(i), kind="output", endpointId=s["deviceId"], channels=2, deviceChannels=[0,1])
                 for i,s in enumerate(streams)]
        prepared = call("setGraph", graph=dict(nodes=nodes, edges=[]))
        check("hardware nodes prepared without any audio source", prepared["hardwareReady"])
        check("shared silent endpoints start", call("start")["running"])
        time.sleep(1.5)
        first = call("status")
        time.sleep(6)
        last = call("status")
        check("primary graph advanced at its clock rate", first["running"] and last["running"] and
              (last["processedFrames"]-first["processedFrames"]) >= cfg["sampleRate"]*5.5 and
              last["mediaClockOutputDeviceId"] == main["id"])
        check("only silence reached final device mix", last["outputSignal"]["peakSinceReset"] == 0 and
              last["outputSignal"]["framesObserved"] == last["processedFrames"])
        active = {p["deviceId"]:p for p in last["endpoints"]}
        check("real master and auxiliary both received callbacks", all(active[d["id"]]["callbackCount"] > 100 and
              not active[d["id"]]["muted"] and active[d["id"]]["sampleRate"] == d["preferredSampleRate"]
              for d in [main, secondary]))
        check("only missing auxiliary reports muted with reason", active[missing]["muted"] and active[missing]["error"]
              and not last["restartRequired"] and not last["error"])
        report["status"] = dict(processedFrames=last["processedFrames"], digitalPeak=last["outputSignal"]["peakSinceReset"],
                                ioMode=last["ioMode"], xruns=last["xruns"], endpoints=[
            {k:v for k,v in p.items() if k != "deviceId"} for p in last["endpoints"]])
        call("stop")
        check("explicit stop closes live rack", not call("status")["running"])
        check("restart reopens and advances", call("start")["running"])
        time.sleep(.3)
        check("restarted graph advances independently of missing aux", call("status")["processedFrames"]>last["processedFrames"])
        call("stop")
    finally:
        if engine.poll() is None:
            try:
                call("shutdown")
                engine.stdin.close()
                engine.wait(timeout=5)
            finally:
                if engine.poll() is None:
                    engine.kill()
                    engine.wait()

if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--engine", required=True, type=pathlib.Path)
    ap.add_argument("--clock-name", required=True)
    ap.add_argument("--aux-name", required=True)
    ap.add_argument("--report", required=True, type=pathlib.Path)
    args = ap.parse_args()
    report = dict(checks=[], passed=False, engineSha256=hashlib.sha256(args.engine.read_bytes()).hexdigest(),
                  boundary="Two real shared-mode render streams, output graph has no signal source. Missing auxiliary is simulated by an absent ID. No audible, real ASIO, capture or physical unplug test.")
    try:
        run(args, report)
        report["passed"] = True
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
