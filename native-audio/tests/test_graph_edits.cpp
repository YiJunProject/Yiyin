#define YIDECK_ENGINE_AUDIT
#define main yideck_engine_entry_unused
#include "../src/engine.cpp"
#undef main
#include <fstream>
#include <filesystem>
#include "test_vst3_ui_connection.h"
#include "test_plugin_analyzer.h"
struct RackGraphEditAudit {
  static Json run(const std::string &path, const std::string &vst3Path) {
    Json checks=Json::array();
    auto check=[&](const char *name,bool pass){checks.push_back({{"name",name},{"passed",pass}});if(!pass)throw std::runtime_error(name);};
    Engine e;
    auto plug=Json{{"instanceId","source"},{"format","vst2"},{"path",path}};
    Json graph={{"nodes",Json::array({{{"id","input"},{"kind","input"},{"plugins",Json::array({plug})}},{{"id","output"},{"kind","output"}}})},
                {"edges",Json::array({{{"id","route"},{"from","input"},{"to","output"}}})}};
    e.command({{"op","setGraph"},{"graph",graph}});
    auto *original=&e.graph->find("source"); auto *originalGraph=e.graph.get();
    HMODULE module=GetModuleHandleW(wide(path).c_str());
    auto privateState=(void(*)(float))GetProcAddress(module,"FixturePrivate");
    auto wait=(void(*)(int))GetProcAddress(module,"FixtureChunkWait");
    check("actual VST2 private fixture exports loaded",privateState&&wait);
    privateState(.875f); wait(80);
    std::atomic<bool> done=false; std::atomic<int> blocks=0,bad=0;
    std::atomic<float> expected=.5f*.875f;
    e.running=true;
    std::thread audio([&]{
      std::array<float,512> in,out; in.fill(1);
      while(!done){e.processBlock(out.data(),in.data(),256,0,0);for(auto v:out)if(std::abs(v-expected.load())>1e-6f)++bad;++blocks;Sleep(1);}
    });
    auto stop=[&]{done=true;audio.join();e.running=false;};
    try {
      while(blocks<5)Sleep(1);
      auto before=blocks.load(); auto state=e.command({{"op","pluginState"},{"instanceId","source"}});
      auto bytes=unbase64(state["chunk"]); float values[2]{};memcpy(values,bytes.data(),sizeof(values));
      check("online VST2 captures unsaved private state on UI thread",values[0]==.5f&&values[1]==.875f);
      check("audio callback advances during slow getChunk",blocks-before>=10);
      check("source waveform continuous during current state capture",bad==0&&e.gateSkips==0);
      auto copied=graph;auto copy=plug;copy["instanceId"]="copy";copy["state"]=state;
      copied["nodes"].push_back({{"id","copy-track"},{"kind","bus"},{"plugins",Json::array({copy})}});
      auto prepared=e.command({{"op","beginGraphEdit"},{"graph",copied}});auto token=prepared["transaction"];
      check("prepare keeps original active graph until disk confirmation",e.graph.get()==originalGraph&&e.activeGraph.load()==originalGraph&&e.editCandidate);
      check("candidate reuses source instance",&e.editCandidate->find("source")==original);
      check("new copy receives complete current private chunk",e.editCandidate->find("copy").state()==state);
      e.command({{"op","rollbackGraphEdit"},{"transaction",token}});
      check("disk-failure rollback keeps exact original graph and plugin",e.graph.get()==originalGraph&&&e.graph->find("source")==original&&!e.editCandidate);
      auto removal=graph;removal["nodes"][0]["plugins"]=Json::array();
      prepared=e.command({{"op","beginGraphEdit"},{"graph",removal}});
      Sleep(25);check("removal prepare does not silence original before persistence",bad==0&&e.activeGraph.load()==originalGraph);
      e.command({{"op","rollbackGraphEdit"},{"transaction",prepared["transaction"]}});
      prepared=e.command({{"op","beginGraphEdit"},{"graph",copied}});
      e.command({{"op","commitGraphEdit"},{"transaction",prepared["transaction"]}});
      check("commit switches graph and retains untouched live source",e.graph.get()!=originalGraph&&&e.graph->find("source")==original);
      Sleep(30);check("prepare commit rollback never gate or dry source signal",bad==0&&e.gateSkips==0);
      bool stale=false;try{e.command({{"op","rollbackGraphEdit"},{"transaction",prepared["transaction"]}});}catch(...){stale=true;}
      check("stale transaction cannot undo committed graph",stale);
      auto invalid=copied;invalid["nodes"].push_back({{"id","bad"},{"kind","bus"},{"plugins",Json::array({{{"instanceId","bad"},{"format","vst2"},{"path","does-not-exist.dll"}}})}});
      auto active=e.activeGraph.load();bool failed=false;try{e.command({{"op","beginGraphEdit"},{"graph",invalid}});}catch(...){failed=true;}
      check("failed new plugin preparation preserves active graph",failed&&e.activeGraph.load()==active&&!e.pendingGraphEdit);
      auto blocking=(void(*)(int))GetProcAddress(module,"FixtureBlockAudio"); blocking(1);
      bool incompatible=false;try{e.command({{"op","pluginState"},{"instanceId","source"}});}catch(const std::exception &error){incompatible=std::string(error.what()).find("snapshotFailed:")==0;}
      blocking(0);
      check("plugin that stalls its audio during getChunk fails instead of claiming seamless copy",incompatible&&e.activeGraph.load()==active);
      stop();
    }catch(...){stop();throw;}
    // Deterministic delay history continuity across an unrelated graph edit.
    Graph old(graph,256,48000);old.nodes[1].outputDelay.setup(17,2);old.edges[0].delay.setup(17,2);
    auto unrelated=graph;unrelated["nodes"].push_back({{"id","unrelated"},{"kind","bus"}});
    Graph next(unrelated,256,48000,&old);
    next.nodes[1].outputDelay.setup(17,2);next.edges[0].delay.setup(17,2);next.retainHistories(old);
    check("unchanged output delay preserves same ring",next.nodes[1].outputDelay.history==old.nodes[1].outputDelay.history);
    check("unchanged route delay preserves same ring",next.edges[0].delay.history==old.edges[0].delay.history);
    old.edges[0].delay.step(.37f,0,0);check("retained delay contains original sample not zero reset",next.edges[0].delay.step(0,0,0)==.37f);
    original=&e.graph->find("source");original->editor(true);
    RECT content{},parent{};GetClientRect(original->editorContent,&content);
    SetWindowPos(original->window,nullptr,0,0,450,300,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
    GetClientRect(original->window,&parent);GetClientRect(original->editorContent,&content);
    check("fixed VST2 keeps full native content inside smaller viewport",content.right==1400&&content.bottom==900&&parent.right<1400&&parent.bottom<900);
    SendMessageW(original->window,WM_VSCROLL,SB_BOTTOM,0);RECT moved{};GetWindowRect(original->editorContent,&moved);POINT pt{moved.left,moved.top};ScreenToClient(original->window,&pt);
    check("fixed VST2 viewport scrolls to clipped content",pt.y<0&&GetScrollPos(original->window,SB_VERT)>0);
    original->editor(false);
    const auto scan = scanPlugin(vst3Path); const auto id = scan[0].at("classId");
    auto v3 = makePlugin({{"instanceId","v3-editor"},{"format","vst3"},{"path",vst3Path},{"classId",id}},2,48000,256);
    v3->editor(true);
    check("VST3 canResize enables real content resize",!v3->fixedEditor&&v3->editorContent);
    SetWindowPos(v3->window,nullptr,0,0,250,130,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
    GetClientRect(v3->editorContent,&content);GetClientRect(v3->window,&parent);
    check("VST3 size constraint applies to content and actual host viewport",content.right==320&&content.bottom==180&&parent.right==320&&parent.bottom==180);
    check("VST3 receives actual initial DPI content scale",(INT_PTR)GetPropW(v3->editorContent,L"FixtureDpiPercent")==GetDpiForWindow(v3->window)*100/96);
    RECT suggested{};GetWindowRect(v3->window,&suggested);
    SendMessageW(v3->window,WM_DPICHANGED,MAKELONG(144,144),(LPARAM)&suggested);
    GetClientRect(v3->editorContent,&content);
    check("synthetic DPI transition notifies VST3 scale and honors resizeView",(INT_PTR)GetPropW(v3->editorContent,L"FixtureDpiPercent")==150&&content.right==600&&content.bottom==360);
    v3->editor(false);
    return {{"passed",true},{"checks",checks},{"count",checks.size()},{"audioBlocks",blocks.load()},{"gateSkips",e.gateSkips.load()},
            {"boundary","Actual VST2 DLL and production Engine callback on owned synthetic thread; no device enumeration or hardware start. Native editor owned by fixture."}};
  }
};
int wmain(int argc,wchar_t **argv){
  if(argc==3 && std::wstring(argv[1])==L"--vst3-ui-messages") return runVst3UiMessageChecks(argv[2]);
  if(argc==5 && std::wstring(argv[1])==L"--plugin-analyzer") return runPluginAnalyzer(argv[2],argv[3],argv[4]);
  if(argc!=4)return 2;SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);initializePlugins();
  try{auto result=RackGraphEditAudit::run(utf8(argv[1]),utf8(argv[3]));std::ofstream(std::filesystem::path(argv[2]))<<result.dump(2);std::cout<<result.dump()<<std::endl;return 0;}
  catch(const std::exception &e){std::ofstream(std::filesystem::path(argv[2]))<<Json{{"passed",false},{"error",e.what()}}.dump(2);std::cerr<<e.what();return 1;}
}
