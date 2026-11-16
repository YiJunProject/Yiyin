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
    return {{"passed",true},{"checks",checks},{"count",checks.size()},{"boundary","Production Engine with injected endpoint metadata and project VST3 fixture only. No device enumeration, configure, audio callback, third-party VST or editor window."}};
  }
};

int wmain(int argc,wchar_t **argv){
  if(argc!=3)return 2;CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);initializePlugins();
  Json checks=Json::array();
  try{auto result=RackGraphEditAudit::run(utf8(argv[1]),checks);std::ofstream(std::filesystem::path(argv[2]))<<result.dump(2);std::cout<<result.dump()<<std::endl;CoUninitialize();return 0;}
  catch(const std::exception &ex){std::ofstream(std::filesystem::path(argv[2]))<<Json{{"passed",false},{"checks",checks},{"error",ex.what()}}.dump(2);std::cerr<<ex.what();CoUninitialize();return 1;}
}
