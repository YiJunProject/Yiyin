#include "vst.h"
#include <windows.h>
#include <atomic>
#include <cstring>
#include <array>
// Purpose-built fixture: private editor state is deliberately not a parameter.
struct LiveFixture;
static LiveFixture *latest = nullptr;
static std::atomic<int> chunkWait{0};
static std::atomic<bool> blockAudio{false}, snapshotting{false};
struct LiveFixture {
  vst_effect_t e{};
  std::atomic<float> gain{.5f}, hidden{.75f};
  std::array<float, 2> chunk{};
  DWORD uiThread = GetCurrentThreadId();
  vst_rect_t rect{0, 0, 900, 1400};
  HWND child = nullptr;
  LiveFixture() {
    e.magic_number = VST_FOURCC('V','s','t','P'); e.control = control; e.set_parameter = set; e.get_parameter = get;
    e.num_inputs = e.num_outputs = 2; e.num_params = 1; e.flags = 1 | (1<<4) | (1<<5);
    e.unique_id = VST_FOURCC('Y','L','i','v'); e.effect_internal = this; e.process_float = process; latest = this;
  }
  static intptr_t VST_FUNCTION_INTERFACE control(vst_effect_t *e, int op, int, intptr_t n, void *p, float) {
    auto *s = (LiveFixture *)e->effect_internal;
    switch(op) {
    case 1: if(latest==s) latest=nullptr; delete s; return 0;
    case 23:
      if(GetCurrentThreadId()!=s->uiThread) return 0;
      snapshotting=true; Sleep(chunkWait.load()); s->chunk = {s->gain.load(), s->hidden.load()}; snapshotting=false; *(void**)p=s->chunk.data(); return sizeof(s->chunk);
    case 24: if(n!=sizeof(s->chunk)) return 0; memcpy(s->chunk.data(),p,n); s->gain=s->chunk[0]; s->hidden=s->chunk[1]; return 1;
    case 13: *(vst_rect_t**)p=&s->rect; return 1;
    case 14: s->child=CreateWindowW(L"STATIC",L"Owned VST2 fixed-size viewport fixture",WS_CHILD|WS_VISIBLE,0,0,1400,900,(HWND)p,nullptr,GetModuleHandle(nullptr),nullptr); return 1;
    case 15: if(s->child)DestroyWindow(s->child); s->child=nullptr; return 1;
    case 35: return 1;
    case 45: strcpy_s((char*)p,32,"Yideck Live State Fixture"); return 1;
    case 47: strcpy_s((char*)p,64,"Yideck isolated test"); return 1;
    default: return 0;
    }
  }
  static void VST_FUNCTION_INTERFACE set(vst_effect_t *e,uint32_t i,float v){if(!i)((LiveFixture*)e->effect_internal)->gain=v;}
  static float VST_FUNCTION_INTERFACE get(vst_effect_t *e,uint32_t){return ((LiveFixture*)e->effect_internal)->gain.load();}
  static void VST_FUNCTION_INTERFACE process(vst_effect_t *e,const float *const *in,float **out,int n){
    while(blockAudio.load()&&snapshotting.load())Sleep(1);
    auto *s=(LiveFixture*)e->effect_internal; float g=s->gain.load()*s->hidden.load();
    for(int c=0;c<2;++c)for(int i=0;i<n;++i)out[c][i]=in[c][i]*g;
  }
};
extern "C" __declspec(dllexport) vst_effect_t *VSTPluginMain(vst_host_callback_t){return &(new LiveFixture)->e;}
extern "C" __declspec(dllexport) void FixturePrivate(float value){if(latest)latest->hidden=value;}
extern "C" __declspec(dllexport) void FixtureChunkWait(int ms){chunkWait=ms;}
extern "C" __declspec(dllexport) void FixtureBlockAudio(int value){blockAudio=value!=0;}
