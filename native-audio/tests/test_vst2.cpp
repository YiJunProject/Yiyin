#include "vst.h"
#include <windows.h>
#include <array>
#include <cstring>
struct Test {
  vst_effect_t e{};
  float gain = .5f;
  std::array<std::array<float, 17>, 2> d{};
  int position = 0;
  HWND editor = nullptr;
  vst_rect_t editorRect{0,0,160,320};
  Test() {
    e.magic_number = VST_FOURCC('V', 's', 't', 'P');
    e.control = control;
    e.set_parameter = set;
    e.get_parameter = get;
    e.num_inputs = 2;
    e.num_outputs = 2;
    e.num_params = 1;
    e.flags = 1 | (1 << 4) | (1 << 5);
    e.unique_id = VST_FOURCC('Y', 'T', 's', 't');
    e.version = 1;
    e.delay = 17;
    e.effect_internal = this;
    e.process_float = process;
  }
  static intptr_t VST_FUNCTION_INTERFACE control(vst_effect_t *e, int op, int index, intptr_t value,
                                                 void *ptr, float) {
    auto *p = (Test *)e->effect_internal;
    switch (op) {
    case 13:
      *(vst_rect_t**)ptr=&p->editorRect;
      return 1;
    case 14:
      p->editor=CreateWindowExW(0,L"STATIC",L"Yideck editor activation fixture",WS_CHILD|WS_VISIBLE|SS_CENTER,0,0,320,160,(HWND)ptr,nullptr,GetModuleHandleW(nullptr),nullptr);
      return p->editor?1:0;
    case 15:
      if(p->editor && IsWindow(p->editor))DestroyWindow(p->editor);
      p->editor=nullptr;
      return 1;
    case 1:
      delete p;
      return 0;
    case 23:
      *(void **)ptr = &p->gain;
      return 4;
    case 24:
      if (value == 4)
        memcpy(&p->gain, ptr, 4);
      return 1;
    case 45:
      strcpy_s((char *)ptr, 32, "Yideck Test Delay Gain");
      return 1;
    case 47:
      strcpy_s((char *)ptr, 64, "Yideck isolated test");
      return 1;
    case 8:
      strcpy_s((char *)ptr, 8, "Gain");
      return 1;
    case 35:
      return 1;
    default:
      return 0;
    }
  }
  static void VST_FUNCTION_INTERFACE set(vst_effect_t *e, uint32_t i, float v) {
    if (i == 0)
      ((Test *)e->effect_internal)->gain = v;
  }
  static float VST_FUNCTION_INTERFACE get(vst_effect_t *e, uint32_t) {
    return ((Test *)e->effect_internal)->gain;
  }
  static void VST_FUNCTION_INTERFACE process(vst_effect_t *e, const float *const *in, float **out,
                                             int n) {
    auto *p = (Test *)e->effect_internal;
    for (int s = 0; s < n; ++s) {
      for (int c = 0; c < 2; ++c) {
        out[c][s] = p->d[c][p->position] * p->gain;
        p->d[c][p->position] = in[c][s];
      }
      p->position = (p->position + 1) % 17;
    }
  }
};
extern "C" __declspec(dllexport) vst_effect_t *VSTPluginMain(vst_host_callback_t) {
  return &(new Test)->e;
}
