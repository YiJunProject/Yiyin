// Isolated protocol regression: fixture modules only; no enumerate/configure/start.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {spawn} = require('node:child_process');
const readline = require('node:readline');
const [engineArg, outputArg, mode = 'fixed'] = process.argv.slice(2);
if (!engineArg || !outputArg) throw Error('engine output [baseline|fixed] required');
const root = path.resolve(__dirname, '../..');
const engine = path.resolve(engineArg), output = path.resolve(outputArg);
const fixture = path.join(root, 'artifacts/native/v1005-bus-build/VST3/Release/Yideck.Test.VST3.Aux.vst3');
const fixtureBinary = path.join(fixture, 'Contents/x86_64-win/Yideck.Test.VST3.Aux.vst3');
const legacy = path.join(root, 'native-audio/build/VST3/Release/Yideck.Test.VST3.vst3');
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const checks = [], requests = [], pending = new Map();
let seq = 0, error = null, stderr = '';
const child = spawn(engine, [], {windowsHide:true, stdio:['pipe','pipe','pipe']});
const exited = new Promise(resolve => child.once('exit', (code, signal) => resolve({code,signal})));
child.stderr.on('data', b => { stderr = (stderr + b.toString()).slice(-3000); });
readline.createInterface({input:child.stdout}).on('line', line => {
  try { const r = JSON.parse(line), p = pending.get(r.id); if(p){pending.delete(r.id);clearTimeout(p.timer);p.resolve(r);} }
  catch(e) { error = 'Invalid engine protocol: ' + e.message; }
});
const request = (op, args = {}) => new Promise((resolve,reject) => {
  const id = ++seq; requests.push(op);
  const timer = setTimeout(() => {pending.delete(id);reject(Error(`Timeout ${op}`));}, 10000);
  pending.set(id,{resolve,reject,timer}); child.stdin.write(JSON.stringify({id,op,...args})+'\n');
});
const good = async(op,args) => {const r = await request(op,args);if(!r.ok)throw Error(`${op}: ${r.error}`);return r.result;};
const check = (name, condition, detail) => {checks.push({name,pass:!!condition,...(detail===undefined?{}:{detail})});if(!condition)throw Error(name);};
const state = gain => { const b = Buffer.alloc(4); b.writeFloatLE(gain); return {component:b.toString('base64')}; };
const graph = (channels,id,saved=state(.375),pluginPath=fixture) => ({
  nodes:[{id:'in',kind:'input',channels,deviceChannels:channels===1?[0]:[0,1],monitor:true},
         {id:'fx',kind:'fx',channels,plugins:[{instanceId:'aux',format:'vst3',path:pluginPath,classId:id,state:saved}]},
         {id:'out',kind:'output',channels:2}],
  edges:[{from:'in',to:'fx',channels},{from:'fx',to:'out',channels}]
});
const ids = ['59494445434B41555842555330303031','59494445434B41555842555330303032','59494445434B41555842555330303033'];
const metadata = async() => {const s=await good('pluginState',{instanceId:'aux'}),b=Buffer.from(s.component,'base64');return {state:s,gain:b.readFloatLE(0),main:b.readInt32LE(4),aux:b.readInt32LE(8),attempts:b.readInt32LE(12),active:b.readInt32LE(16)};};
const audio = async() => {const input=Array(128).fill(0);input[0]=1;input[1]=.25;return (await good('offline',{frames:64,input})).samples;};
(async() => {
 try {
  const initial=await good('status');check('Engine starts without hardware/audio callbacks', !initial.running && initial.callbackCount===0);
  for(const [index,label] of [[0,'matched auxiliary'],[2,'plugin-proposed auxiliary']]) {
   await good('clearGraph'); const r=await request('setGraph',{graph:graph(1,ids[index])});
   if(mode==='baseline') check(`Baseline mono ${label} reproduces rejection`,!r.ok&&r.error.includes('rejects requested mono/stereo bus arrangement'),r.error);
   else {
    check(`Mono ${label} negotiates successfully`,r.ok,r.error);
    const m=await metadata();check(`Mono ${label} retains mono main/aux and inactive sidechain with bounded retry`,m.main===1&&m.aux===1&&m.active===0&&m.attempts===2,{...m,state:undefined});
    const a=await audio();check(`Mono ${label} restores state and processes zero auxiliary safely`,m.gain===.375&&Math.abs(a[0]-.375)<1e-6&&Math.abs(a[1]-.375)<1e-6);
   }
  }
  await good('clearGraph');await good('setGraph',{graph:graph(1,ids[1])});
  let m=await metadata();check('Fixed stereo auxiliary remains stereo on mono track without fallback',m.main===1&&m.aux===2&&m.active===0&&m.attempts===1,{...m,state:undefined});
  let a=await audio();check('Fixed stereo auxiliary mono audio and restored parameters remain correct',Math.abs(a[0]-.375)<1e-6&&Math.abs(a[1]-.375)<1e-6);
  await good('clearGraph');await good('setGraph',{graph:graph(2,ids[0])});
  m=await metadata();check('Stereo main and auxiliary preserved',m.main===2&&m.aux===2&&m.active===0&&m.attempts===1);
  a=await audio();check('Stereo channels preserve independent samples',Math.abs(a[0]-.375)<1e-6&&Math.abs(a[1]-.09375)<1e-6);
  if(mode!=='baseline') {
   await good('setParameter',{instanceId:'aux',parameterId:0,value:.625});
   m=await metadata();check('Stopped parameter flush preserved after negotiation',m.gain===.625);
   await good('setGraph',{graph:graph(2,ids[0],state(.125))});m=await metadata();
   check('Graph rebuild retains live state over stale serialized state',m.gain===.625&&m.main===2&&m.aux===2);
   const rejected=await request('setGraph',{graph:graph(1,'59494445434B46585445535430303031',state(.25),legacy)});
   check('Truly stereo-only processor rejects mono without silent upmix',!rejected.ok&&rejected.error.includes('rejects requested mono/stereo bus arrangement'),rejected.error);
   m=await metadata();a=await audio();check('Failed mono negotiation leaves previous graph and parameters intact',m.gain===.625&&m.main===2&&Math.abs(a[0]-.625)<1e-6);
   await good('clearGraph');
   await good('setGraph',{graph:graph(2,'59494445434B46585445535430303031',state(.375),legacy)});
   const legacyStatus=await good('status');a=await audio();
   check('Existing stereo processor latency is unchanged',legacyStatus.graphLatencyFrames===17,legacyStatus.graphLatencyFrames);
   check('Existing stereo processor signal and state retain delay alignment',a.slice(0,34).every(x=>x===0)&&Math.abs(a[34]-.375)<1e-6&&Math.abs(a[35]-.09375)<1e-6);
  }
  const final=await good('status');check('All checks remain stopped with zero hardware callbacks',!final.running&&final.callbackCount===0);
  check('No enumeration configure or start operation sent',!requests.some(x=>['devices','configure','start'].includes(x)));
  await good('shutdown');child.stdin.end();const exit=await exited;check('Owned fixture engine exits normally',exit.code===0,exit);
 } catch(e) {error=e.stack;child.kill();await exited;}
 finally {
  for(const p of pending.values())clearTimeout(p.timer);
  fs.mkdirSync(path.dirname(output),{recursive:true});
  fs.writeFileSync(output,JSON.stringify({mode,passed:checks.filter(x=>x.pass).length,total:checks.length,error,engine,engineSha256:hash(engine),fixtureSha256:hash(fixtureBinary),scope:'Isolated synthetic VST3 processors; no physical audio device, no third-party plugin',checks,requests,stderr},null,2));
  console.log(JSON.stringify({output,passed:checks.filter(x=>x.pass).length,total:checks.length,error}));if(error)process.exitCode=1;
 }
})();
