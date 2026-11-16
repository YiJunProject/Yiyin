#pragma once
#include "vst3_ui_connection.h"
using namespace Steinberg;
using namespace Steinberg::Vst;
static int runVst3UiMessageChecks(const wchar_t *outputPath) {
  struct Endpoint : U::Implements<U::Directly<IConnectionPoint>> {
    IConnectionPoint *peer = nullptr;
    DWORD owner = GetCurrentThreadId();
    int count = 0;
    bool wrongThread = false;
    tresult PLUGIN_API connect(IConnectionPoint *other) override { peer=other;return kResultOk; }
    tresult PLUGIN_API disconnect(IConnectionPoint *) override { peer=nullptr;return kResultOk; }
    tresult PLUGIN_API notify(IMessage *) override { ++count;wrongThread |= owner!=GetCurrentThreadId();return kResultOk; }
  };
  struct Message : U::Implements<U::Directly<IMessage>> {
    int &alive;
    explicit Message(int &value) : alive(value) { ++alive; }
    ~Message() override { --alive; }
    const char *PLUGIN_API getMessageID() override { return "analyzer-request"; }
    void PLUGIN_API setMessageID(const char *) override {}
    IAttributeList *PLUGIN_API getAttributes() override { return nullptr; }
  };
  Json checks=Json::array();
  auto check=[&](const char *name,bool passed){checks.push_back({{"name",name},{"passed",passed}});if(!passed)throw std::runtime_error(name);};
  try {
    auto source=owned(new Endpoint),target=owned(new Endpoint);
    auto connection=owned(new Vst3UiConnection(source));
    check("connection established",connection->connect(target)==kResultOk&&source->peer==connection.get());
    int alive=0;auto message=owned(new Message(alive));
    check("UI notify remains immediate",connection->notify(message)==kResultOk&&target->count==1);
    tresult queued=kResultFalse;
    std::thread producer([&]{queued=connection->notify(message);connection->drain();});producer.join();
    check("background notify waits for owning UI thread",queued==kResultOk&&target->count==1);
    message=nullptr;check("queued message lifetime retained",alive==1);
    connection->drain();check("UI drain delivers once and releases message",target->count==2&&!target->wrongThread&&alive==0);
    connection->drain();check("empty drain does not replay",target->count==2);
    message=owned(new Message(alive));int accepted=0,rejected=0;
    std::thread flood([&]{for(int i=0;i<257;++i)(connection->notify(message)==kResultOk?accepted:rejected)++;});flood.join();
    check("pending messages are bounded",accepted==256&&rejected==1);
    message=nullptr;check("bounded queue retains messages until closed",alive==1);
    check("disconnect clears retained queue and source",connection->disconnect(target)==kResultOk&&source->peer==nullptr&&alive==0);
    connection->drain();check("closed queue never delivers stale messages",target->count==2);
    message=owned(new Message(alive));
    std::thread late([&]{queued=connection->notify(message);});late.join();
    check("late background notify rejected after disconnect",queued==kResultFalse);
    auto result=Json{{"passed",true},{"count",checks.size()},{"checks",checks},{"boundary","real production Vst3UiConnection, worker/UI threads; no audio or device output"}};
    std::ofstream(std::filesystem::path(outputPath))<<result.dump(2);std::cout<<result.dump()<<std::endl;return 0;
  }catch(const std::exception &e){std::ofstream(std::filesystem::path(outputPath))<<Json{{"passed",false},{"error",e.what()},{"checks",checks}}.dump(2);return 1;}
}
