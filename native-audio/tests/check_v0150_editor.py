"""Actual owned native fixture HWND checks; no audio endpoint, user input, or production window access."""
import ctypes as c,ctypes.wintypes as w,hashlib,json,pathlib,queue,subprocess,sys,threading,time,traceback
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
root=pathlib.Path(__file__).resolve().parents[2];out=root/'evidence/v0150-native/editor';out.mkdir(parents=True,exist_ok=True)
engine=root/'native-audio/bin-v0150/Yideck.Audio.Engine.exe';fixture=root/'native-audio/test-bin-v0150/Yideck.Test.VST2.dll'
u=c.windll.user32;g=c.windll.gdi32
u.SetProcessDpiAwarenessContext(c.c_void_p(-4))
u.CreateWindowExW.argtypes=[w.DWORD,w.LPCWSTR,w.LPCWSTR,w.DWORD,c.c_int,c.c_int,c.c_int,c.c_int,w.HWND,w.HMENU,w.HINSTANCE,c.c_void_p];u.CreateWindowExW.restype=w.HWND
u.GetWindow.argtypes=[w.HWND,w.UINT];u.GetWindow.restype=w.HWND
u.GetForegroundWindow.restype=w.HWND;u.GetWindowLongPtrW.argtypes=[w.HWND,c.c_int];u.GetWindowLongPtrW.restype=c.c_ssize_t
for name in ['IsWindow','IsWindowVisible','IsIconic','SetForegroundWindow','DestroyWindow']:getattr(u,name).argtypes=[w.HWND]
u.ShowWindow.argtypes=[w.HWND,c.c_int];u.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
u.GetWindowDC.restype=w.HDC;u.GetWindowDC.argtypes=[w.HWND];u.ReleaseDC.argtypes=[w.HWND,w.HDC]
u.PrintWindow.argtypes=[w.HWND,w.HDC,w.UINT];u.GetWindowRect.argtypes=[w.HWND,c.POINTER(w.RECT)]
g.CreateCompatibleDC.restype=w.HDC;g.CreateCompatibleDC.argtypes=[w.HDC];g.CreateCompatibleBitmap.restype=w.HBITMAP;g.CreateCompatibleBitmap.argtypes=[w.HDC,c.c_int,c.c_int]
g.SelectObject.restype=w.HGDIOBJ;g.SelectObject.argtypes=[w.HDC,w.HGDIOBJ];g.DeleteObject.argtypes=[w.HGDIOBJ];g.DeleteDC.argtypes=[w.HDC]
class BI(c.Structure):_fields_=[('size',w.DWORD),('width',w.LONG),('height',w.LONG),('planes',w.WORD),('bits',w.WORD),('compression',w.DWORD),('sizeImage',w.DWORD),('x',w.LONG),('y',w.LONG),('used',w.DWORD),('important',w.DWORD)]
g.GetDIBits.argtypes=[w.HDC,w.HBITMAP,w.UINT,w.UINT,c.c_void_p,c.c_void_p,w.UINT]
def capture(hwnd,path):
 r=w.RECT();u.GetWindowRect(hwnd,c.byref(r));width=r.right-r.left;height=r.bottom-r.top
 dc=u.GetWindowDC(hwnd);mem=g.CreateCompatibleDC(dc);bm=g.CreateCompatibleBitmap(dc,width,height);old=g.SelectObject(mem,bm)
 try:
  ok=u.PrintWindow(hwnd,mem,2);bi=BI(c.sizeof(BI),width,-height,1,32,0,0,0,0,0,0);buf=c.create_string_buffer(width*height*4)
  g.GetDIBits(mem,bm,0,height,buf,c.byref(bi),0);Image.frombuffer('RGB',(width,height),buf.raw,'raw','BGRX',0,1).save(path)
  return dict(printWindow=bool(ok),width=width,height=height,sha256=hashlib.sha256(path.read_bytes()).hexdigest())
 finally:g.SelectObject(mem,old);g.DeleteObject(bm);g.DeleteDC(mem);u.ReleaseDC(hwnd,dc)
owner_ready=queue.Queue();owner_stop=threading.Event()
def owner_thread():
 hwnd=u.CreateWindowExW(0,'STATIC','Yideck isolated rack owner',0x00CF0000,40,40,420,220,None,None,None,None)
 u.ShowWindow(hwnd,4);owner_ready.put(hwnd);msg=w.MSG()
 while not owner_stop.is_set():
  while u.PeekMessageW(c.byref(msg),None,0,0,1):u.TranslateMessage(c.byref(msg));u.DispatchMessageW(c.byref(msg))
  time.sleep(.005)
 u.DestroyWindow(hwnd)
t=threading.Thread(target=owner_thread);t.start();owner=owner_ready.get(timeout=5)
p=subprocess.Popen([str(engine)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8');responses=queue.Queue()
threading.Thread(target=lambda:[responses.put(x) for x in p.stdout],daemon=True).start();seq=0;checks=[]
def check(ok,name):
 checks.append(dict(name=name,passed=bool(ok)))
 if not ok:raise AssertionError(name)
def ask(op,**kw):
 global seq
 seq+=1;p.stdin.write(json.dumps(dict(id=seq,op=op,**kw))+'\n');p.stdin.flush();r=json.loads(responses.get(timeout=20))
 if not r['ok']:raise RuntimeError(r)
 return r['result']
report=dict(engineSha256=hashlib.sha256(engine.read_bytes()).hexdigest(),fixtureSha256=hashlib.sha256(fixture.read_bytes()).hexdigest(),hardwareOpened=False,scope='Actual self-owned compiled VST2 fixture HWND; no commercial plugin claim')
try:
 check(bool(owner),'isolated rack owner HWND created')
 ask('setGraph',graph=dict(nodes=[dict(id='fx',kind='fx',channels=2,plugins=[dict(instanceId='editor-fixture',format='vst2',path=str(fixture),classId='1498706804')])],edges=[]))
 u.SetForegroundWindow(owner);report['allowForeground']=bool(u.AllowSetForegroundWindow(p.pid))
 opened=ask('editor',instanceId='editor-fixture',show=True,ownerWindowHandle=owner);report['opened']=opened;hwnd=opened['windowHandle'];time.sleep(.15)
 check(bool(u.IsWindowVisible(hwnd)),'real fixture editor visible')
 check(u.GetWindow(hwnd,4)==owner and opened['ownerWindowHandle']==owner,'editor owns requested rack HWND')
 check(u.GetForegroundWindow()==hwnd and opened['foregroundActivated'],'user-requested editor obtains foreground')
 check(not (u.GetWindowLongPtrW(hwnd,-20)&8),'editor is not permanently topmost')
 report['capture']=capture(hwnd,out/'editor.png');check(report['capture']['printWindow'],'owned editor renders through PrintWindow')
 u.ShowWindow(hwnd,6);time.sleep(.1);check(bool(u.IsIconic(hwnd)),'owned editor minimizes')
 u.SetForegroundWindow(owner);u.AllowSetForegroundWindow(p.pid)
 reopened=ask('editor',instanceId='editor-fixture',show=True,ownerWindowHandle=owner)
 check(reopened['windowHandle']==hwnd and not u.IsIconic(hwnd),'repeated click restores same minimized editor')
 check(u.GetForegroundWindow()==hwnd and reopened['foregroundActivated'],'restored editor is foreground')
 u.PostMessageW(hwnd,0x10,0,0);time.sleep(.1);check(not u.IsWindowVisible(hwnd),'close hides editor without destroying plugin')
 u.SetForegroundWindow(owner);u.AllowSetForegroundWindow(p.pid)
 again=ask('editor',instanceId='editor-fixture',show=True,ownerWindowHandle=owner)
 check(again['windowHandle']==hwnd and u.IsWindowVisible(hwnd),'hidden editor reopens in same HWND')
 check(u.GetWindow(hwnd,4)==owner and not (u.GetWindowLongPtrW(hwnd,-20)&8),'reopen preserves owner without topmost')
 ask('editor',instanceId='editor-fixture',show=False);check(not u.IsWindowVisible(hwnd),'explicit hide works')
 check(ask('status')['callbackCount']==0,'editor test opened no audio stream')
 ask('shutdown');p.stdin.close();p.wait(10);check(p.returncode==0,'owned engine closes cleanly')
 check(not u.IsWindow(hwnd),'owned editor destroyed on shutdown');report['passed']=True
except Exception as e:report.update(passed=False,error=str(e),traceback=traceback.format_exc())
finally:
 if p.poll() is None:p.kill();p.wait()
 owner_stop.set();t.join(5);report.update(checks=checks,checkCount=len(checks),stderr=p.stderr.read());(out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['checks','stderr']},ensure_ascii=False));sys.exit(0 if report.get('passed') else 1)
