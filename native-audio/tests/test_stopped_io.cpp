#define YIDECK_ENGINE_AUDIT
#define main yideck_engine_entry_unused
#include "../src/engine.cpp"
#undef main
#include <fstream>
#include <filesystem>

// Reuses the existing test-only Engine friend. No configure, device discovery,
// audio thread or editor window is permitted in this fixture.
struct RackGraphEditAudit {
  static Json run(const std::string &fixture, Json &checks) {
    auto check=[&](const char *name,bool passed){
      checks.push_back({{"name",name},{"passed",passed}});
      if(!passed) throw std::runtime_error(name);
    };
    Engine e;
    e.endpointSpecs={{"isolated-endpoint","fixture",2,0,false,true}};
    e.config={{"api","wasapi"},{"outputDeviceId","isolated-endpoint"}};
    auto plugin=Json{{"instanceId","source"},{"format","vst3"},{"path",fixture},
                     {"classId","59494445434B46585445535430303031"}};
    Json graph={{"nodes",Json::array({
      {{"id","bus"},{"kind","bus"},{"channels",2},{"plugins",Json::array({plugin})}},
      {{"id","out"},{"kind","output"},{"channels",2},{"endpointId","isolated-endpoint"},{"deviceChannels",{0,1}}}})},
      {"edges",Json::array({{{"id","main"},{"from","bus"},{"to","out"}}})}};
    auto result=e.command({{"op","setGraph"},{"graph",graph}});
    check("initial stopped graph matches selected two-channel endpoint",result["hardwareReady"]==true);
    e.command({{"op","stop"}}); e.command({{"op","clearGraph"}});
    check("stop and clear retain old selected endpoint range",e.endpointSpecs.size()==1&&e.endpointSpecs[0].channels==2&&!e.graph);
    auto wider=graph;wider["nodes"][1]["deviceChannels"]={2,3};
    result=e.command({{"op","setGraph"},{"graph",wider}});
    check("stopped new graph can prepare plugins beyond stale endpoint range",result["hardwareReady"]==false&&!e.graphEndpointsReady);
    check("stopped range mismatch has explicit non-plugin reason",result.value("hardwareIssue","")=="channel-range");
    check("plugin is loaded for editor path without opening a window",e.command({{"op","pluginInfo"},{"instanceId","source"}}).is_object());
    e.command({{"op","editor"},{"instanceId","source"},{"show",false}});
    auto *source=&e.graph->find("source");
    e.command({{"op","setParameter"},{"instanceId","source"},{"parameterId",0},{"value",.75}});
    const auto state=e.command({{"op","pluginState"},{"instanceId","source"}});
    auto added=wider;auto extra=plugin;extra["instanceId"]="added";added["nodes"][0]["plugins"].push_back(extra);
    auto pending=e.command({{"op","beginGraphEdit"},{"graph",added}});
    check("stopped add prepares without claiming hardware readiness",pending["hardwareReady"]==false);
    result=e.command({{"op","commitGraphEdit"},{"transaction",pending["transaction"]}});
    check("commit retains existing plugin object and current parameters",&e.graph->find("source")==source&&e.command({{"op","pluginState"},{"instanceId","source"}})==state);
    check("commit retains unresolved IO status",!e.graphEndpointsReady&&e.command({{"op","status"}}).value("hardwareIssue","")=="channel-range");
    pending=e.command({{"op","beginGraphEdit"},{"graph",graph}});
    check("candidate valid routing does not alter active readiness",pending["hardwareReady"]==true&&!e.graphEndpointsReady);
    e.command({{"op","rollbackGraphEdit"},{"transaction",pending["transaction"]}});
    check("rollback retains unresolved IO and original plugin",!e.graphEndpointsReady&&&e.graph->find("source")==source);
    // The readiness assertion above guarantees start exits before any device call.
    std::string startError;
    try {e.command({{"op","start"}});}catch(const std::exception &ex){startError=ex.what();}
    check("unresolved stopped graph cannot start hardware",startError=="Graph hardware endpoints are unresolved; configure and reapply graph before start"&&!e.running&&!e.audio&&!e.bridge);
    e.endpointSpecs[0].channels=4;e.outCh=4;
    result=e.command({{"op","setGraph"},{"graph",added}});
    check("matching confirmed range restores readiness without replacing plugins",result["hardwareReady"]==true&&result.value("hardwareIssue","").empty()&&&e.graph->find("source")==source);
    e.endpointSpecs[0].channels=2;e.outCh=2;
    e.command({{"op","setGraph"},{"graph",graph}});auto *old=e.activeGraph.load();
    e.running=true;bool rejected=false;
    try {e.command({{"op","beginGraphEdit"},{"graph",wider}});}catch(const std::exception &ex){rejected=std::string(ex.what())=="Graph channel exceeds its selected endpoint range";}
    e.running=false;
    check("running range mismatch still rejects before graph publication",rejected&&e.activeGraph.load()==old&&!e.pendingGraphEdit);
    for(auto channel:{-1,256}){
      auto invalid=graph;invalid["nodes"][1]["deviceChannels"]={0,channel};bool invalidRejected=false;
      try {e.command({{"op","setGraph"},{"graph",invalid}});}catch(...){invalidRejected=true;}
      check(channel<0?"negative channel remains structurally invalid":"channel above engine maximum remains structurally invalid",invalidRejected&&e.activeGraph.load()==old);
    }
    auto missing=graph;missing["nodes"][1]["endpointId"]="unconfigured-endpoint";
    result=e.command({{"op","setGraph"},{"graph",missing}});
    check("missing endpoint retains existing stopped editing behavior",result["hardwareReady"]==false);
    check("fixture never discovers hardware starts callbacks or opens an editor",!e.audio&&!e.bridge&&e.callbacks==0&&!e.graph->find("source").window);
    Engine matrix;
    matrix.config={{"api","wasapi"},{"ioMode","multi"}};
    matrix.endpointSpecs={{"capture","a",2,0,true,false},{"main","a",2,0,false,true},{"phones","b",2,2,false,false}};
    matrix.inCh=2; matrix.outCh=4;
    auto route=[](const char *id, int left, int right) {
      return Json{{"endpointId",id},{"deviceChannels",{left,right}}};
    };
    Json capture={{"id","in"},{"kind","input"},{"channels",2},{"hardwareRoutes",{route("capture",0,1)}}};
    Json out={{"id","out"},{"kind","output"},{"channels",2},
              {"hardwareRoutes",{route("main",0,1),route("phones",1,0)}}};
    auto processingNode=capture;
    processingNode["plugins"]=Json::array({plugin});
    result=matrix.command({{"op","setGraph"},{"graph",{
      {"nodes",Json::array({processingNode,out})},{"edges",Json::array({{{"from","in"},{"to","out"}}})}}}});
    check("multi-card graph prepares both independent stereo targets",result["hardwareReady"]==true);
    auto *originalGraph=matrix.graph.get(); auto *originalPlugin=&matrix.graph->find("source");
    const auto matrixState=matrix.command({{"op","pluginState"},{"instanceId","source"}});
    std::vector<float> input(128);
    for(int i=0;i<64;++i) { input[i*2]=.5f; input[i*2+1]=.25f; }
    auto block=[&]() { return matrix.command({{"op","offline"},{"frames",64},{"input",input}}).at("samples").get<std::vector<float>>(); };
    auto before=block();
    check("actual delayed VST3 stereo signal reaches both targets with independent channel order",
          before[252]==.25f&&before[253]==.125f&&before[254]==.125f&&before[255]==.25f);
    auto apply=[&](const Json &in,const Json &output) {
      return matrix.command({{"op","setHardwareRoutes"},{"nodes",Json::array({in,output})}});
    };
    out["hardwareRoutes"]={route("main",0,1),route("phones",-1,0)};
    matrix.running=true;
    result=apply(capture,out);
    matrix.running=false;
    auto after=block(); bool continuous=true;
    for(int i=0;i<64;++i)
      continuous &= after[i*4]==.25f && after[i*4+1]==.125f && after[i*4+2]==.125f && after[i*4+3]==0;
    check("hot mute changes one mirror channel while every main sample remains continuous",continuous);
    check("route change preserves graph, delay memory and live plugin identity/state",
          matrix.graph.get()==originalGraph&&&matrix.graph->find("source")==originalPlugin&&
          matrix.command({{"op","pluginState"},{"instanceId","source"}})==matrixState);
    auto invalid=out; invalid["hardwareRoutes"]=Json::array({route("main",6,1)});
    matrix.running=true; rejected=false; try {apply(capture,invalid);} catch(...) {rejected=true;} matrix.running=false;
    check("invalid running matrix leaves previous routing audible",rejected&&block()==after);
    out["hardwareRoutes"]={route("phones",0,1),route("missing",0,1),route("phones",0,1)};
    capture["hardwareRoutes"]=Json::array({route("capture",-1,1)});
    apply(capture,out); after=block();
    check("missing target and disabled input lane stay silent; duplicate mirror does not double gain",
          after[252]==0&&after[253]==0&&after[254]==0&&after[255]==.125f);
    out["hardwareRoutes"]=Json::array(); apply(capture,out); after=block();
    check("clearing all output targets yields digital silence",std::all_of(after.begin(),after.end(),[](float v){return v==0;}));
    capture["hardwareRoutes"]=Json::array({route("capture",0,1)});
    auto mainOnly=out; mainOnly["hardwareRoutes"]=Json::array({route("main",0,1)});
    auto mirrored=out; mirrored["hardwareRoutes"]=Json::array({route("main",0,1),route("phones",1,0)});
    apply(capture,mainOnly); block();
    std::atomic<int> observed{0}; std::atomic<bool> intact{true};
    matrix.running=true;
    {
      std::jthread callback([&](std::stop_token stop){
        std::vector<float> samples(64*4);
        while(!stop.stop_requested()) {
          matrix.processBlock(samples.data(),input.data(),64,0,0);
          bool mirror=samples[2]!=0;
          for(int i=0;i<64;++i)
            if(samples[4*i]!=.25f || samples[4*i+1]!=.125f ||
               samples[4*i+2]!=(mirror?.125f:0.f) || samples[4*i+3]!=(mirror?.25f:0.f)) intact=false;
          ++observed;
        }
      });
      while(!observed.load()) std::this_thread::yield();
      for(int i=0;i<256;++i) apply(capture,i%2 ? mainOnly : mirrored);
    }
    matrix.running=false;
    check("256 live edits during synthetic callbacks preserve every main sample and apply whole blocks",
          observed>0 && intact.load() && matrix.gateSkips==0 && originalGraph==matrix.activeGraph.load());
    return {{"passed",true},{"checks",checks},{"count",checks.size()},{"syntheticCallbackCount",observed.load()},{"boundary","Production Engine with injected endpoint metadata, offline blocks, a synthetic callback thread and project VST3 fixture. No physical device, commercial plugin or editor window."}};
  }
};

int wmain(int argc,wchar_t **argv){
  if(argc!=3)return 2;CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);initializePlugins();
  Json checks=Json::array();
  try{auto result=RackGraphEditAudit::run(utf8(argv[1]),checks);std::ofstream(std::filesystem::path(argv[2]))<<result.dump(2);std::cout<<result.dump()<<std::endl;CoUninitialize();return 0;}
  catch(const std::exception &ex){std::ofstream(std::filesystem::path(argv[2]))<<Json{{"passed",false},{"checks",checks},{"error",ex.what()}}.dump(2);std::cerr<<ex.what();CoUninitialize();return 1;}
}
