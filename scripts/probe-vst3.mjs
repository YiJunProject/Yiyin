import fs from 'node:fs';
import path from 'node:path';
import {spawn,execFileSync} from 'node:child_process';
import readline from 'node:readline';
const [engine,scan,pluginRoot,out]=process.argv.slice(2),results=[];
const files=fs.readdirSync(pluginRoot).filter(x=>x.endsWith('.vst3'));
for(const name of files){
  const file=path.join(pluginRoot,name);
  let inventory;
  try{inventory=JSON.parse(execFileSync(scan,[file],{encoding:'utf8',timeout:50000,windowsHide:true}));}catch(e){results.push({name,scanFailed:true,error:e.message.slice(0,300)});continue;}
  for(const plugin of inventory.plugins||[]){
    const p=spawn(engine,[],{windowsHide:true,stdio:['pipe','pipe','pipe']}),pending=new Map();
    let diagnostic='';p.stderr.on('data',chunk=>{if(diagnostic.length<2400)diagnostic+=chunk.toString();});let seq=0;
    readline.createInterface({input:p.stdout}).on('line',line=>{try{const j=JSON.parse(line);pending.get(j.id)?.(j);pending.delete(j.id);}catch{}});
    const request=(op,fields={})=>new Promise((resolve,reject)=>{const id=++seq,timer=setTimeout(()=>{p.kill();reject(new Error(op+' timeout'));},18000);pending.set(id,j=>{clearTimeout(timer);resolve(j);});p.stdin.write(JSON.stringify({id,op,...fields})+'\n');});
    try{
      const reply=await request('setGraph',{graph:{nodes:[{id:'source',kind:'input',channels:2,deviceChannels:[0,1],monitor:true,plugins:[{instanceId:'effect',format:'vst3',path:file,classId:plugin.classId}]},{id:'output',kind:'output',channels:2,deviceChannels:[0,1]}],edges:[{id:'route',from:'source',to:'output',source:'post',gain:1,channels:2}]}});
      await request('shutdown');p.stdin.end();
      await new Promise(resolve=>setTimeout(resolve,30));
      results.push({name,classId:plugin.classId,loaded:reply.ok,error:reply.error,diagnostic});
    }catch(e){results.push({name,loaded:false,error:e.message});}finally{if(p.exitCode===null)p.kill();}
  }
}
fs.writeFileSync(out,JSON.stringify(results,null,2));console.log(JSON.stringify(results));
