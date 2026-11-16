#pragma once
// Existing native host consumer: task-owned plugin, synthetic processing, no audio device.
static void captureAnalyzer(HWND window,const std::filesystem::path &file) {
  RECT r{};GetClientRect(window,&r);int w=r.right,h=r.bottom;
  auto dc=GetDC(window), memory=CreateCompatibleDC(dc);BITMAPINFO info{};
  info.bmiHeader={sizeof(BITMAPINFOHEADER),w,-h,1,32,BI_RGB,(DWORD)(w*h*4),0,0,0,0};
  void *pixels=nullptr;auto bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&pixels,nullptr,0);
  auto old=SelectObject(memory,bitmap);BitBlt(memory,0,0,w,h,dc,0,0,SRCCOPY);
  BITMAPFILEHEADER header{0x4d42,(DWORD)(sizeof(BITMAPFILEHEADER)+sizeof(BITMAPINFOHEADER)+w*h*4),0,0,sizeof(BITMAPFILEHEADER)+sizeof(BITMAPINFOHEADER)};
  std::ofstream out(file,std::ios::binary);out.write((char*)&header,sizeof(header));out.write((char*)&info.bmiHeader,sizeof(info.bmiHeader));out.write((char*)pixels,w*h*4);
  SelectObject(memory,old);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(window,dc);
}
static int runPluginAnalyzer(const wchar_t *pluginPath,const wchar_t *outputPath,const wchar_t *statePath) {
  auto output=std::filesystem::path(outputPath);std::filesystem::create_directories(output);
  SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);initializePlugins();
  // Deny activation only on this owned thread; never touch the production process.
  auto hook=SetWindowsHookExW(WH_CBT,[](int code,WPARAM w,LPARAM l)->LRESULT{return code==HCBT_ACTIVATE?1:CallNextHookEx(nullptr,code,w,l);},nullptr,GetCurrentThreadId());
  try {
    auto path=utf8(pluginPath);auto scan=scanPlugin(path);Json config={{"instanceId","isolated-analyzer"},{"format","vst3"},{"path",path},{"classId",scan[0]["classId"]}};
    if(std::wstring(statePath)!=L"-"){std::ifstream file{std::filesystem::path(statePath)};file>>config["state"];}
    auto p=makePlugin(config,2,48000,256);auto before=p->describe();p->editor(true);
    SetWindowPos(p->window,HWND_TOPMOST,100,100,0,0,SWP_NOSIZE|SWP_NOACTIVATE);
    std::atomic<bool> stop=false;std::atomic<int> blocks=0;std::atomic<double> outputEnergy=0;
    std::thread worker([&]{std::array<float,256> a{},b{},c{},d{};float *in[]{a.data(),b.data()},*out[]{c.data(),d.data()};uint64_t pos=0;
      auto start=std::chrono::steady_clock::now();while(!stop){for(int i=0;i<256;++i)a[i]=b[i]=pos<48000?0.f:.5f*(float)sin((pos+i)*2*3.141592653589793*440/48000);
        p->process(in,out,256,pos);double energy=0;for(float x:c)energy+=x*x;outputEnergy+=energy;pos+=256;++blocks;
        std::this_thread::sleep_until(start+std::chrono::microseconds(pos*1000000/48000));}});
    auto start=std::chrono::steady_clock::now();bool first=false,last=false;
    try {while(std::chrono::steady_clock::now()-start<std::chrono::seconds(4)){
      MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}p->idle();
      auto elapsed=std::chrono::duration<double>(std::chrono::steady_clock::now()-start).count();
      if(!first&&elapsed>.6){captureAnalyzer(p->editorContent,output/L"silence.bmp");first=true;}
      if(!last&&elapsed>3.5){captureAnalyzer(p->editorContent,output/L"signal.bmp");last=true;}Sleep(5);
    }}catch(...){stop=true;worker.join();throw;}
    stop=true;worker.join();p->idle();auto after=p->describe();
    Json result={{"before",before},{"after",after},{"blocks",blocks.load()},{"outputEnergy",outputEnergy.load()},{"boundary","actual owned VST3 UI plus synthetic 48kHz stereo processor thread; no hardware/audio/MIDI output; task-only visible window, no activation"}};
    std::ofstream(output/L"report.json")<<result.dump(2);p->editor(false);p.reset();UnhookWindowsHookEx(hook);CoUninitialize();return 0;
  }catch(const std::exception&e){std::ofstream(output/L"report.json")<<Json{{"error",e.what()}}.dump(2);UnhookWindowsHookEx(hook);CoUninitialize();return 1;}
}
