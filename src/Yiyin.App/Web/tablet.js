'use strict';
let state=null,socket=null,retry=null,closing=false;
const $=id=>document.getElementById(id), toast=text=>{ $('toast').textContent=text;setTimeout(()=>{$('toast').textContent='';},3500); };
const rack=createLightDeckRack({state:()=>state,ready:()=>socket?.readyState===WebSocket.OPEN,send:message=>{if(socket?.readyState!==WebSocket.OPEN)return false;socket.send(JSON.stringify(message));return true;},closeInputs:()=>{},hideMusic:()=>{},refreshStatus:()=>{},otherSelected:()=>false,fallback:()=>{},toast});
function connect(){clearTimeout(retry);if(socket)socket.close();socket=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/ws`);const current=socket;
current.onmessage=event=>{if(current!==socket)return;const next=JSON.parse(event.data);state=next;$('pair').hidden=true;$('connection').textContent='已连接 · 电脑状态同步中';rack.update(state);};
current.onclose=()=>{if(current!==socket)return;rack.cancel();state=null;rack.refreshEnabled();$('connection').textContent='连接已断开';$('pair').hidden=false;if(!closing)retry=setTimeout(connect,4000);};
current.onerror=()=>{$('connection').textContent='连接不可用，请输入当前连接码';};}
$('pair').addEventListener('submit',async event=>{event.preventDefault();try{const response=await fetch('/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:$('code').value.trim()})});if(!response.ok)throw new Error('连接码无效，请查看电脑当前显示的连接码。');$('pair-error').textContent='';connect();}catch(error){$('pair-error').textContent=error.message;}});
$('reconnect').onclick=connect;$('fullscreen').onclick=()=>{if(document.fullscreenElement)document.exitFullscreen();else document.documentElement.requestFullscreen().catch(error=>toast(error.message));};
document.addEventListener('visibilitychange',()=>{if(document.hidden)rack.cancel();});window.addEventListener('pagehide',()=>{closing=true;clearTimeout(retry);rack.cancel();socket?.close();});connect();
