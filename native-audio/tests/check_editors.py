"""Only newly-created engine plugin windows; no keyboard/mouse or production-window access."""
import ctypes as c,ctypes.wintypes as w,json,pathlib,subprocess,threading,queue,time,sys,hashlib,os
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];out=root/'evidence/v0130-native-audio/native-editors-physical';out.mkdir(exist_ok=True)
engine=root/'native-audio/bin/Yideck.Audio.Engine.exe';u=c.windll.user32;g=c.windll.gdi32
if os.environ.get('YIDECK_AUDIT_ENGINE'):
 engine=pathlib.Path(os.environ['YIDECK_AUDIT_ENGINE']);out=root/'evidence/v0130-native-audio/native-editors-ascii';out.mkdir(exist_ok=True)
if os.environ.get('YIDECK_AUDIT_LABEL'):
 out=root/'evidence/v0130-native-audio'/os.environ['YIDECK_AUDIT_LABEL'];out.mkdir(exist_ok=True)
u.SetProcessDpiAwarenessContext(c.c_void_p(-4))
u.GetWindowDC.restype=w.HDC;u.GetWindowDC.argtypes=[w.HWND];u.ReleaseDC.argtypes=[w.HWND,w.HDC]
u.PrintWindow.argtypes=[w.HWND,w.HDC,w.UINT];u.GetWindowRect.argtypes=[w.HWND,c.POINTER(w.RECT)];u.IsWindowVisible.argtypes=[w.HWND];u.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
g.CreateCompatibleDC.restype=w.HDC;g.CreateCompatibleDC.argtypes=[w.HDC];g.CreateCompatibleBitmap.restype=w.HBITMAP;g.CreateCompatibleBitmap.argtypes=[w.HDC,c.c_int,c.c_int]
g.SelectObject.restype=w.HGDIOBJ;g.SelectObject.argtypes=[w.HDC,w.HGDIOBJ];g.DeleteObject.argtypes=[w.HGDIOBJ];g.DeleteDC.argtypes=[w.HDC]
class BI(c.Structure):_fields_=[('size',w.DWORD),('width',w.LONG),('height',w.LONG),('planes',w.WORD),('bits',w.WORD),('compression',w.DWORD),('sizeImage',w.DWORD),('x',w.LONG),('y',w.LONG),('used',w.DWORD),('important',w.DWORD)]
g.GetDIBits.argtypes=[w.HDC,w.HBITMAP,w.UINT,w.UINT,c.c_void_p,c.c_void_p,w.UINT]
def capture(hwnd,path):
 rect=w.RECT();u.GetWindowRect(hwnd,c.byref(rect));width=rect.right-rect.left;height=rect.bottom-rect.top
 if width<1 or height<1:raise RuntimeError('Invalid owned editor dimensions')
 dc=u.GetWindowDC(hwnd);mem=g.CreateCompatibleDC(dc);bitmap=g.CreateCompatibleBitmap(dc,width,height);old=g.SelectObject(mem,bitmap)
 try:
  ok=u.PrintWindow(hwnd,mem,2);info=BI(c.sizeof(BI),width,-height,1,32,0,0,0,0,0,0);buf=c.create_string_buffer(width*height*4)
  g.GetDIBits(mem,bitmap,0,height,buf,c.byref(info),0);im=Image.frombuffer('RGB',(width,height),buf.raw,'raw','BGRX',0,1);im.save(path)
  return {'printWindow':bool(ok),'width':width,'height':height,'extrema':im.getextrema(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
 finally:g.SelectObject(mem,old);g.DeleteObject(bitmap);g.DeleteDC(mem);u.ReleaseDC(hwnd,dc)
original=json.loads((root/'evidence/v0130-native-audio/installed-consumer/report.json').read_text(encoding='utf-8'))
v2=json.loads((root/'evidence/v0130-native-audio/installed-vst2/report.json').read_text(encoding='utf-8'))
candidates=[next(x for x in original['plugins'] if x.get('selected')==n) for n in ['Micro','ValhallaRoom','CLA-76 Stereo']]+[v2['plugins'][0]]
candidates+=json.loads((root/'evidence/v0130-native-audio/installed-fabpro/report.json').read_text(encoding='utf-8'))['plugins']
if len(sys.argv)>1:candidates=[x for x in candidates if sys.argv[1].lower() in x['selected'].lower()]
report={'engineSha256':hashlib.sha256(engine.read_bytes()).hexdigest(),'scope':'Actual owned native plugin HWNDs, PrintWindow captures; offline only, no physical device or user input', 'editors':[]}
for ix,source in enumerate(candidates):
 desc=next(d for d in source['scan']['plugins'] if d['name']==source['selected']);row={'name':desc['name'],'format':desc['format']};report['editors'].append(row)
 log=open(out/f'{ix}-stderr.log','w',encoding='utf-8');p=subprocess.Popen([str(engine)],cwd=str(pathlib.Path(desc['path']).parent) if len(sys.argv)>2 else None,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8');q=queue.Queue();threading.Thread(target=lambda proc=p:[q.put(x) for x in proc.stdout],daemon=True).start();seq=0
 def ask(op,**kw):
  global seq
  seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(q.get(timeout=20))
  if not r['ok']:raise RuntimeError(str(r))
  return r['result']
 try:
  plug=dict(instanceId='p',format=desc['format'],path=desc['path'],classId=desc['classId']);ask('setGraph',graph={'nodes':[{'id':'p','kind':'fx','channels':2,'plugins':[plug]}],'edges':[]})
  state=ask('pluginState',instanceId='p');row['open']=ask('editor',instanceId='p',show=True);hwnd=row['open']['windowHandle'];time.sleep(.7)
  row['capture']=capture(hwnd,out/f'{ix}-{desc["format"]}-editor.png');u.PostMessageW(hwnd,0x10,0,0);time.sleep(.2);row['closeHides']=not bool(u.IsWindowVisible(hwnd))
  row['reopen']=ask('editor',instanceId='p',show=True);time.sleep(.2);row['sameWindow']=hwnd==row['reopen']['windowHandle'];row['reopenVisible']=bool(u.IsWindowVisible(hwnd))
  ask('editor',instanceId='p',show=False);ask('setPluginState',instanceId='p',state=state);row['stateRestoresAfterEditor']=bool(ask('pluginState',instanceId='p'))
  ask('shutdown');p.stdin.close();p.wait(timeout=10);row['exited']=p.returncode==0;row['passed']=all([row['capture']['printWindow'],row['closeHides'],row['sameWindow'],row['reopenVisible'],row['stateRestoresAfterEditor'],row['exited']])
 except Exception as e:row['error']=str(e);row['passed']=False
 finally:
  if p.poll() is None:p.kill();p.wait()
  (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
 print(row['name'],row.get('passed'),row.get('error',''),flush=True)
