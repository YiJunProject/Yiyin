#include "plugins.h"
// Xaymar's VST_VERSION enum must precede the SDK's macro of the same name.
#include "vst.h"
#include "vst3_ui_connection.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include <array>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <wincrypt.h>
using namespace Steinberg;
using namespace Steinberg::Vst;
std::wstring wide(const std::string &s) {
  if (s.empty())
    return {};
  int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
  if (!n)
    throw std::runtime_error("Invalid UTF-8");
  std::wstring w(n, 0);
  MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), w.data(), n);
  return w;
}
std::string utf8(const std::wstring &w) {
  if (w.empty())
    return {};
  int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
  std::string s(n, 0);
  WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
  return s;
}
std::string base64(const void *p, size_t n) {
  if (!n)
    return {};
  DWORD size = 0;
  CryptBinaryToStringA((const BYTE *)p, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF,
                       nullptr, &size);
  std::string s(size, 0);
  CryptBinaryToStringA((const BYTE *)p, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF,
                       s.data(), &size);
  s.resize(size);
  return s;
}
std::vector<unsigned char> unbase64(const std::string &s) {
  if (s.empty())
    return {};
  DWORD n = 0;
  if (!CryptStringToBinaryA(s.c_str(), (DWORD)s.size(), CRYPT_STRING_BASE64, nullptr, &n, nullptr,
                            nullptr) ||
      n > 64 * 1024 * 1024)
    throw std::runtime_error("Invalid or oversized state");
  std::vector<unsigned char> b(n);
  if (!CryptStringToBinaryA(s.c_str(), (DWORD)s.size(), CRYPT_STRING_BASE64, b.data(), &n, nullptr,
                            nullptr))
    throw std::runtime_error("Invalid state");
  return b;
}
static void layoutEditor(Plugin &p) {
  if (!p.window || !p.editorContent || p.editorLayout) return;
  p.editorLayout = true;
  RECT r{}; GetClientRect(p.window, &r);
  if (p.fixedEditor) {
    SCROLLINFO x{sizeof(x), SIF_RANGE | SIF_PAGE}, y{sizeof(y), SIF_RANGE | SIF_PAGE};
    x.nMax = std::max(0, p.editorWidth - 1); x.nPage = r.right;
    y.nMax = std::max(0, p.editorHeight - 1); y.nPage = r.bottom;
    SetScrollInfo(p.window, SB_HORZ, &x, TRUE); SetScrollInfo(p.window, SB_VERT, &y, TRUE);
    // One scrollbar can reduce the other viewport dimension.
    GetClientRect(p.window, &r); x.nPage = r.right; y.nPage = r.bottom;
    SetScrollInfo(p.window, SB_HORZ, &x, TRUE); SetScrollInfo(p.window, SB_VERT, &y, TRUE);
    SetWindowPos(p.editorContent, nullptr, -GetScrollPos(p.window, SB_HORZ), -GetScrollPos(p.window, SB_VERT),
                 p.editorWidth, p.editorHeight, SWP_NOZORDER | SWP_NOACTIVATE);
  } else {
    ShowScrollBar(p.window, SB_BOTH, FALSE);
    SetWindowPos(p.editorContent, nullptr, 0, 0, r.right, r.bottom, SWP_NOZORDER | SWP_NOACTIVATE);
    p.resized(r.right, r.bottom);
  }
  p.editorLayout = false;
}
static LRESULT CALLBACK editorProc(HWND w, UINT msg, WPARAM wp, LPARAM lp) {
  if (msg == WM_NCCREATE) {
    auto *cs = (CREATESTRUCTW *)lp;
    SetWindowLongPtrW(w, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
  }
  auto *p = (Plugin *)GetWindowLongPtrW(w, GWLP_USERDATA);
  if (msg == WM_CLOSE) {
    ShowWindow(w, SW_HIDE);
    return 0;
  }
  if (msg == WM_SIZE && wp != SIZE_MINIMIZED && p) {
    layoutEditor(*p);
    return 0;
  }
  if (msg == WM_DPICHANGED && p) {
    auto *r = (RECT *)lp;
    SetWindowPos(w, nullptr, r->left, r->top, r->right-r->left, r->bottom-r->top, SWP_NOZORDER | SWP_NOACTIVATE);
    p->dpiChanged(HIWORD(wp)); layoutEditor(*p); return 0;
  }
  if (p && p->fixedEditor && (msg == WM_HSCROLL || msg == WM_VSCROLL || msg == WM_MOUSEWHEEL)) {
    int bar = msg == WM_HSCROLL ? SB_HORZ : SB_VERT;
    SCROLLINFO s{sizeof(s), SIF_ALL}; GetScrollInfo(w, bar, &s);
    int pos = s.nPos;
    int op = msg == WM_MOUSEWHEEL ? -1 : LOWORD(wp);
    if (op == SB_THUMBTRACK || op == SB_THUMBPOSITION) pos = s.nTrackPos;
    else if (op == SB_LINEUP) pos -= 24;
    else if (op == SB_LINEDOWN) pos += 24;
    else if (op == SB_PAGEUP) pos -= s.nPage;
    else if (op == SB_PAGEDOWN) pos += s.nPage;
    else if (op == SB_TOP) pos = 0;
    else if (op == SB_BOTTOM) pos = s.nMax;
    else if (op == -1) pos -= GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA * 48;
    s.fMask = SIF_POS; s.nPos = pos; SetScrollInfo(w, bar, &s, TRUE); layoutEditor(*p); return 0;
  }
  if (msg == WM_NCDESTROY && p) { p->window = nullptr; p->editorContent = nullptr; }
  return DefWindowProcW(w, msg, wp, lp);
}
static HWND createEditor(const std::string &name, Plugin *plugin) {
  static bool registered = false;
  if (!registered) {
    WNDCLASSW c{};
    c.lpfnWndProc = editorProc;
    c.hInstance = GetModuleHandle(nullptr);
    c.lpszClassName = L"YideckPluginEditor";
    c.hCursor = LoadCursor(nullptr, IDC_ARROW);
    c.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(1));
    RegisterClassW(&c);
    registered = true;
  }
  auto w = CreateWindowExW(0, L"YideckPluginEditor", wide("Yideck - " + name).c_str(),
                         WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 640, 480, plugin->editorOwner,
                         nullptr, GetModuleHandle(nullptr), plugin);
  if (w) plugin->editorContent = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                                                  0, 0, 640, 480, w, nullptr, GetModuleHandle(nullptr), nullptr);
  return w;
}
void setPluginEditorOwner(Plugin &plugin, HWND owner) {
  if (owner && (!IsWindow(owner) || owner == plugin.window))
    throw std::runtime_error("Plugin editor owner window is unavailable");
  plugin.editorOwner = owner ? GetAncestor(owner, GA_ROOT) : nullptr;
  if (plugin.window && IsWindow(plugin.window)) {
    SetLastError(0);
    SetWindowLongPtrW(plugin.window, GWLP_HWNDPARENT, (LONG_PTR)plugin.editorOwner);
    if (GetLastError()) throw std::runtime_error("Cannot assign plugin editor owner");
  }
}
bool activatePluginEditor(Plugin &plugin) {
  if (!plugin.window || !IsWindow(plugin.window))
    throw std::runtime_error("Plugin editor window is unavailable");
  // Explicit user action only. The parent grants foreground permission before
  // IPC. Never attach unrelated input queues or make the editor always-on-top.
  ShowWindow(plugin.window, IsIconic(plugin.window) ? SW_RESTORE : SW_SHOW);
  BringWindowToTop(plugin.window);
  SetActiveWindow(plugin.window);
  return SetForegroundWindow(plugin.window) || GetForegroundWindow() == plugin.window;
}
static void sizeEditor(HWND w, int width, int height) {
  auto *p = (Plugin *)GetWindowLongPtrW(w, GWLP_USERDATA);
  if (width <= 0 || height <= 0 || width > 32768 || height > 32768) return;
  if (p) { p->editorWidth = width; p->editorHeight = height; }
  RECT r{0, 0, width, height};
  AdjustWindowRectExForDpi(&r, (DWORD)GetWindowLongPtr(w, GWL_STYLE), FALSE, 0, GetDpiForWindow(w));
  MONITORINFO monitor{sizeof(monitor)}; GetMonitorInfoW(MonitorFromWindow(w, MONITOR_DEFAULTTONEAREST), &monitor);
  auto outerW = r.right-r.left, outerH = r.bottom-r.top;
  if (p && p->fixedEditor) { outerW = std::min(outerW, monitor.rcWork.right-monitor.rcWork.left); outerH = std::min(outerH, monitor.rcWork.bottom-monitor.rcWork.top); }
  RECT current{}; GetWindowRect(w, &current);
  const auto x = p && p->fixedEditor ? std::clamp(current.left, monitor.rcWork.left, monitor.rcWork.right-outerW) : current.left;
  const auto y = p && p->fixedEditor ? std::clamp(current.top, monitor.rcWork.top, monitor.rcWork.bottom-outerH) : current.top;
  SetWindowPos(w, nullptr, p && p->fixedEditor ? x : current.left, p && p->fixedEditor ? y : current.top,
               outerW, outerH, SWP_NOZORDER | SWP_NOACTIVATE);
  if (p) layoutEditor(*p);
}
void initializePlugins() {
  static IPtr<HostApplication> host = owned(new HostApplication);
  PluginContextFactory::instance().setPluginContext(host);
  PlugProvider::setErrorStream(&std::cerr);
}

class V3 final : public Plugin {
  struct Handler : U::Implements<U::Directly<IComponentHandler, IPlugFrame>> {
    V3 *p;
    explicit Handler(V3 *x) : p(x) {}
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue v) override {
      p->queue(id, v);
      return kResultOk;
    }
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32 flags) override {
      if (flags & (kLatencyChanged | kIoChanged | kReloadComponent))
        p->restart = true;
      return kResultOk;
    }
    tresult PLUGIN_API resizeView(IPlugView *view, ViewRect *r) override {
      if (!p->window || !r)
        return kInvalidArgument;
      sizeEditor(p->window, r->getWidth(), r->getHeight());
      return view->onSize(r);
    }
  };
  VST3::Hosting::Module::Ptr module;
  IPtr<Vst3UiPlugProvider> provider;
  IPtr<IComponent> comp;
  IPtr<IEditController> ctrl;
  IPtr<IAudioProcessor> proc;
  IPtr<Handler> handler;
  IPtr<IPlugView> view;
  HostProcessData data;
  ParameterChanges changes{4096};
  ProcessContext context{};
  struct Change {
    uint32_t id;
    double value;
  };
  std::array<Change, 4096> queueItems{};
  std::atomic<uint32_t> wr{0}, rd{0};
  std::atomic<bool> restart{false};
  std::string path, cid, name, vendor, version;
  int channels, block;
  double rate;
  bool active = false;
  void queue(uint32_t id, double v) {
    auto w = wr.load(std::memory_order_relaxed), r = rd.load(std::memory_order_acquire);
    if (w - r >= queueItems.size()) {
      restart = true;
      return;
    }
    queueItems[w % queueItems.size()] = {id, std::clamp(v, 0.0, 1.0)};
    wr.store(w + 1, std::memory_order_release);
  }

public:
  V3(const Json &j, int ch, double sr, int bs)
      : path(j.at("path")), cid(j.value("classId", "")), channels(ch), block(bs), rate(sr) {
    std::string err;
    module = VST3::Hosting::Module::create(path, err);
    if (!module)
      throw std::runtime_error("VST3 load: " + err);
    auto factory = module->getFactory();
    for (auto &c : factory.classInfos())
      if (c.category() == kVstAudioEffectClass && (cid.empty() || c.ID().toString() == cid)) {
        cid = c.ID().toString();
        name = c.name();
        vendor = c.vendor();
        version = c.version();
        provider = owned(new Vst3UiPlugProvider(factory, c, true));
        break;
      }
    if (!provider || !provider->initialize())
      throw std::runtime_error("VST3 class initialization failed: " + cid);
    comp = provider->getComponentPtr();
    ctrl = provider->getControllerPtr();
    FUnknownPtr<IAudioProcessor> ap(comp);
    if (!ap)
      throw std::runtime_error("VST3 has no audio processor");
    proc = ap;
    handler = owned(new Handler(this));
    if (ctrl)
      ctrl->setComponentHandler(handler);
    SpeakerArrangement layout = ch == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
    std::vector<SpeakerArrangement> inputLayouts, outputLayouts;
    auto readLayouts = [&](BusDirection direction, std::vector<SpeakerArrangement> &layouts) {
      const int count = comp->getBusCount(kAudio, direction);
      if (count < 1 || count > 64)
        throw std::runtime_error("VST3 requires main audio input and output");
      layouts.resize(count);
      for (int b = 0; b < count; ++b) {
        BusInfo info{};
        if (proc->getBusArrangement(direction, b, layouts[b]) != kResultOk ||
            comp->getBusInfo(kAudio, direction, b, info) != kResultOk ||
            info.channelCount != SpeakerArr::getChannelCount(layouts[b]) ||
            (b == 0 && info.busType != kMain))
          throw std::runtime_error("VST3 audio bus layout unavailable or inconsistent");
      }
    };
    auto readAllLayouts = [&] {
      readLayouts(kInput, inputLayouts);
      readLayouts(kOutput, outputLayouts);
    };
    auto mainLayoutsMatch = [&] { return inputLayouts[0] == layout && outputLayouts[0] == layout; };
    auto requestLayouts = [&] {
      const auto result = proc->setBusArrangements(inputLayouts.data(), (int32)inputLayouts.size(),
                                                  outputLayouts.data(), (int32)outputLayouts.size());
      if (result != kResultOk && result != kResultFalse)
        throw std::runtime_error("VST3 audio bus arrangement negotiation failed");
      // A false result may still adjust the main buses and propose a different
      // auxiliary layout. Read back every bus before deciding compatibility.
      readAllLayouts();
      if (!mainLayoutsMatch())
        return false;
      if (result == kResultOk)
        return true;
      const auto accepted = proc->setBusArrangements(inputLayouts.data(), (int32)inputLayouts.size(),
                                                    outputLayouts.data(), (int32)outputLayouts.size());
      readAllLayouts();
      return accepted == kResultOk && mainLayoutsMatch();
    };
    readAllLayouts();
    inputLayouts[0] = outputLayouts[0] = layout;
    bool layoutAccepted = requestLayouts();
    if (!layoutAccepted) {
      // Some effects require an unused sidechain to follow the main bus shape;
      // others require its default shape, which was deliberately tried first.
      bool auxiliaryChanged = false;
      for (int dir = 0; dir < 2; ++dir) {
        auto &layouts = dir == kInput ? inputLayouts : outputLayouts;
        for (int b = 1; b < (int)layouts.size(); ++b) {
          BusInfo info{};
          if (comp->getBusInfo(kAudio, (BusDirection)dir, b, info) != kResultOk)
            throw std::runtime_error("VST3 auxiliary bus information unavailable");
          if (info.busType == kAux && layouts[b] != layout) {
            layouts[b] = layout;
            auxiliaryChanged = true;
          }
        }
      }
      inputLayouts[0] = outputLayouts[0] = layout;
      if (auxiliaryChanged)
        layoutAccepted = requestLayouts();
    }
    if (!layoutAccepted)
      throw std::runtime_error("VST3 rejects requested mono/stereo bus arrangement");
    for (int dir = 0; dir < 2; ++dir) {
      int n = comp->getBusCount(kAudio, (BusDirection)dir);
      for (int b = 0; b < n; ++b)
        comp->activateBus(kAudio, (BusDirection)dir, b, b == 0);
      int ne = comp->getBusCount(kEvent, (BusDirection)dir);
      for (int b = 0; b < ne; ++b)
        comp->activateBus(kEvent, (BusDirection)dir, b, false);
    }
    BusInfo bi{};
    if (comp->getBusInfo(kAudio, kInput, 0, bi) != kResultOk || bi.channelCount != ch)
      throw std::runtime_error("VST3 input channel mismatch");
    if (comp->getBusInfo(kAudio, kOutput, 0, bi) != kResultOk || bi.channelCount != ch)
      throw std::runtime_error("VST3 output channel mismatch");
    ProcessSetup setup{kRealtime, kSample32, bs, sr};
    if (proc->setupProcessing(setup) != kResultOk || !data.prepare(*comp, bs, kSample32))
      throw std::runtime_error("VST3 processing preparation failed");
    data.processMode = kRealtime;
    data.symbolicSampleSize = kSample32;
    data.inputParameterChanges = &changes;
    data.processContext = &context;
    context.sampleRate = sr;
    context.tempo = 120;
    context.timeSigNumerator = 4;
    context.timeSigDenominator = 4;
    if (j.contains("state") && !j["state"].empty())
      restore(j["state"]);
    if (comp->setActive(true) != kResultOk)
      throw std::runtime_error("VST3 activation failed");
    active = true;
    auto processing = proc->setProcessing(true);
    if (processing != kResultOk && processing != kNotImplemented)
      throw std::runtime_error("VST3 processing activation failed");
  }
  ~V3() {
    if (view) {
      view->removed();
      view->setFrame(nullptr);
      view = nullptr;
    }
    if (window)
      DestroyWindow(window);
    if (active) {
      proc->setProcessing(false);
      comp->setActive(false);
    }
    if (ctrl)
      ctrl->setComponentHandler(nullptr);
    handler = nullptr;
    proc = nullptr;
    ctrl = nullptr;
    comp = nullptr;
    provider = nullptr;
    module = nullptr;
  }
  int latency() const override { return (int)proc->getLatencySamples(); }
  bool needsRestart() override { return restart.load(); }
  void acknowledgeRestart() override { restart = false; }
  void idle() override { provider->drain(); }
  bool runningStateSafe() const override { return true; }
  void flushParametersStopped() override {
    if (rd.load() != wr.load()) {
      float zero = 0;
      float *p[]{&zero, &zero};
      process(p, p, 0, 0);
    }
  }
  void resized(int width, int height) override {
    if (window && view && view->canResize() == kResultOk) {
      ViewRect r{0, 0, width, height};
      view->checkSizeConstraint(&r);
      view->onSize(&r);
      if (r.getWidth() != width || r.getHeight() != height) {
        sizeEditor(window, r.getWidth(), r.getHeight());
        SetWindowPos(editorContent, nullptr, 0, 0, r.getWidth(), r.getHeight(), SWP_NOZORDER | SWP_NOACTIVATE);
      }
    }
  }
  void dpiChanged(unsigned dpi) override {
    if (view) {
      FUnknownPtr<IPlugViewContentScaleSupport> scale(view);
      if (scale) scale->setContentScaleFactor(float(dpi) / 96.f);
    }
  }
  void parameter(uint32_t id, double value) override {
    if (!ctrl)
      throw std::runtime_error("Plugin has no controller");
    if (ctrl->setParamNormalized(id, value) != kResultOk)
      throw std::runtime_error("Unknown parameter");
    queue(id, value);
  }
  void process(float **in, float **out, int n, uint64_t position) override {
    changes.clearQueue();
    auto r = rd.load(std::memory_order_relaxed), w = wr.load(std::memory_order_acquire);
    while (r < w) {
      auto &c = queueItems[r % queueItems.size()];
      int32 idx = 0, point = 0;
      changes.addParameterData(c.id, idx)->addPoint(0, c.value, point);
      ++r;
    }
    rd.store(r, std::memory_order_release);
    for (int b = 0; b < data.numInputs; ++b) {
      data.inputs[b].silenceFlags = 0;
      for (int c = 0; c < data.inputs[b].numChannels; ++c) {
        auto *p = data.inputs[b].channelBuffers32[c];
        if (b == 0 && c < channels)
          std::copy_n(in[c], n, p);
        else
          std::fill_n(p, n, 0.f);
      }
    }
    for (int b = 0; b < data.numOutputs; ++b) {
      data.outputs[b].silenceFlags = 0;
      for (int c = 0; c < data.outputs[b].numChannels; ++c)
        std::fill_n(data.outputs[b].channelBuffers32[c], n, 0.f);
    }
    context.projectTimeSamples = position;
    context.continousTimeSamples = position;
    context.projectTimeMusic = position / rate * 2;
    context.state = ProcessContext::kPlaying | ProcessContext::kTempoValid |
                    ProcessContext::kTimeSigValid | ProcessContext::kContTimeValid;
    data.numSamples = n;
    if (proc->process(data) != kResultOk) {
      for (int c = 0; c < channels; ++c)
        std::fill_n(out[c], n, 0.f);
      restart = true;
      return;
    }
    for (int c = 0; c < channels; ++c)
      std::copy_n(data.outputs[0].channelBuffers32[c], n, out[c]);
  }
  Json state() override {
    MemoryStream a, b;
    if (comp->getState(&a) != kResultOk)
      throw std::runtime_error("VST3 component state failed");
    Json s{{"component", base64(a.getData(), a.getSize())}};
    if (ctrl && ctrl->getState(&b) == kResultOk)
      s["controller"] = base64(b.getData(), b.getSize());
    return s;
  }
  void restore(const Json &s) override {
    if (s.contains("component")) {
      auto a = unbase64(s.at("component"));
      MemoryStream stream(a.data(), a.size());
      if (comp->setState(&stream) != kResultOk)
        throw std::runtime_error("VST3 component restore failed");
      if (ctrl) {
        stream.seek(0, IBStream::kIBSeekSet, nullptr);
        auto status = ctrl->setComponentState(&stream);
        if (status != kResultOk && status != kNotImplemented)
          throw std::runtime_error("VST3 controller component restore failed: " +
                                   std::to_string(status));
      }
    }
    if (ctrl && s.contains("controller")) {
      auto b = unbase64(s.at("controller"));
      MemoryStream stream(b.data(), b.size());
      if (ctrl->setState(&stream) != kResultOk)
        throw std::runtime_error("VST3 controller restore failed");
    }
  }
  Json describe() override {
    Json p = Json::array();
    if (ctrl)
      for (int i = 0; i < ctrl->getParameterCount(); ++i) {
        ParameterInfo q{};
        if (ctrl->getParameterInfo(i, q) == kResultOk)
          p.push_back({{"id", q.id},
                       {"name", StringConvert::convert(q.title)},
                       {"defaultValue", q.defaultNormalizedValue},
                       {"value", ctrl->getParamNormalized(q.id)}});
      }
    return {{"format", "vst3"},
            {"path", path},
            {"classId", cid},
            {"name", name},
            {"vendor", vendor},
            {"version", version},
            {"inputChannels", channels},
            {"outputChannels", channels},
            {"latencyFrames", latency()},
            {"parameters", p}};
  }
  void editor(bool show) override {
    if (!show) {
      if (window)
        ShowWindow(window, SW_HIDE);
      return;
    }
    if (!ctrl)
      throw std::runtime_error("No plugin editor");
    if (view && (!window || !IsWindow(window))) {
      view->removed();
      view->setFrame(nullptr);
      view = nullptr;
      window = nullptr;
    }
    if (!view) {
      std::string stage = "createView";
      try {
        view = owned(ctrl->createView(ViewType::kEditor));
        if (!view || view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultOk)
          throw std::runtime_error("No Windows plugin editor");
        stage = "attach";
        window = createEditor(name, this);
        if (!window) throw std::runtime_error("Cannot create VST3 editor host window");
        view->setFrame(handler);
        fixedEditor = view->canResize() != kResultOk;
        dpiChanged(GetDpiForWindow(window));
        if (view->attached(editorContent, kPlatformTypeHWND) != kResultOk)
          throw std::runtime_error("Plugin editor attach failed");
        stage = "getSize";
        ViewRect r{};
        if (view->getSize(&r) == kResultOk)
          sizeEditor(window, r.getWidth(), r.getHeight());
      } catch (const std::exception &e) {
        if (view) {
          view->setFrame(nullptr);
          view = nullptr;
        }
        if (window) {
          DestroyWindow(window);
          window = nullptr;
        }
        std::string detail=e.what();
        if(detail.find("boostWaves")!=std::string::npos&&detail.find("codecvt")!=std::string::npos)
          throw std::runtime_error("Waves 原生界面无法处理当前宿主路径。请将易控安装到仅含英文字符的路径（如 Programs/Yideck）后重试；当前音频处理链保留。详细原因："+detail);
        throw std::runtime_error("VST3 editor " + stage + ": " + detail);
      }
    }
    ShowWindow(window, SW_SHOW);
  }
};

class V2 final : public Plugin {
  HMODULE module = nullptr;
  vst_effect_t *effect = nullptr;
  std::string path, name;
  int channels, block;
  double rate;
  bool active = false;
  bool editorAttached = false;
  std::atomic<bool> restart{false};
  int32_t chosenId = 0;
  struct Change {
    uint32_t index;
    float value;
  };
  std::array<Change, 4096> pending{};
  std::atomic<uint32_t> wr{0}, rd{0};
  static thread_local int32_t openingId;
  static intptr_t VST_FUNCTION_INTERFACE host(vst_effect_t *e, int32_t op, int32_t index,
                                              int64_t value, const char *ptr, float v) {
    auto *p = e ? (V2 *)e->host_internal : nullptr;
    switch (op) {
    case 1:
      return 2400;
    case 2:
      return p ? p->chosenId : openingId;
    case 0:
      return 1;
    case 16:
      return p ? (intptr_t)p->rate : 48000;
    case 17:
      return p ? p->block : 256;
    case 23:
      return 2;
    case 32:
      if (ptr)
        strcpy_s((char *)ptr, 64, "Yideck");
      return 1;
    case 33:
      if (ptr)
        strcpy_s((char *)ptr, 64, "Yideck Audio Rack");
      return 1;
    case 34:
      return 130;
    case 15:
      if (p && p->window) {
        sizeEditor(p->window, index, (int)value);
        return 1;
      }
      return 0;
    case 13:
      if (p)
        p->restart = true;
      return 1;
    case 37:
      if (ptr && (!strcmp(ptr, "sizeWindow") || !strcmp(ptr, "shellCategory") ||
                  !strcmp(ptr, "shellCategoryCurID")))
        return 1;
      return 0;
    case 38:
      return 1;
    default:
      return 0;
    }
  }
  intptr_t call(int op, int index = 0, intptr_t value = 0, void *ptr = nullptr, float f = 0) {
    return effect->control(effect, op, index, value, ptr, f);
  }

public:
  V2(const Json &j, int ch, double sr, int bs, bool scanning = false)
      : path(j.at("path")), channels(ch), block(bs), rate(sr) {
    try {
      module = LoadLibraryW(wide(path).c_str());
      if (!module)
        throw std::runtime_error("Cannot load x64 VST2 library");
      auto entry =
          (vst_effect_t * (*)(vst_host_callback_t)) GetProcAddress(module, "VSTPluginMain");
      if (!entry)
        entry = (vst_effect_t * (*)(vst_host_callback_t)) GetProcAddress(module, "main");
      if (!entry)
        throw std::runtime_error("No VST2 entry point");
      chosenId = (int32_t)std::stoll(j.value("classId", "0"));
      openingId = chosenId;
      effect = entry(host);
      if (!effect || effect->magic_number != VST_FOURCC('V', 's', 't', 'P')) {
        effect = nullptr;
        throw std::runtime_error("Invalid VST2 effect");
      }
      effect->host_internal = this;
      call(0);
      openingId = 0;
      char text[256]{};
      call(45, 0, 0, text);
      name = text;
      if (name.empty())
        name = utf8(std::filesystem::path(wide(path)).stem().wstring());
      if (scanning)
        return;
      if (effect->num_inputs != ch || effect->num_outputs != ch || !effect->process_float)
        throw std::runtime_error("VST2 rejects requested mono/stereo processing");
      call(10, 0, 0, nullptr, (float)sr);
      call(11, 0, bs);
      if (j.contains("state") && !j["state"].empty())
        restore(j["state"]);
      flushParametersStopped();
      call(12, 0, 1);
      call(71);
      active = true;
    } catch (...) {
      openingId = 0;
      if (effect) {
        call(1);
        effect = nullptr;
      }
      if (module) {
        FreeLibrary(module);
        module = nullptr;
      }
      throw;
    }
  }
  ~V2() {
    if (effect) {
      if (editorAttached) call(15);
      if (window) DestroyWindow(window);
      if (active) {
        call(72);
        call(12, 0, 0);
      }
      call(1);
      effect = nullptr;
    }
    if (module)
      FreeLibrary(module);
  }
  int latency() const override { return std::max(0, effect->delay); }
  void prepareRunningState() override {
    // Commands and editor messages execute on the native UI thread. Drain only
    // host parameter writes already submitted before this snapshot. The audio
    // thread owns set_parameter/process; it is never gated for getChunk.
    const auto target = wr.load(std::memory_order_acquire);
    const auto until = GetTickCount64() + 1000;
    while (rd.load(std::memory_order_acquire) < target) {
      if (GetTickCount64() >= until)
        throw std::runtime_error("snapshotFailed: VST2 pending parameters were not processed");
      Sleep(1);
    }
  }
  bool needsRestart() override { return restart.load(); }
  void acknowledgeRestart() override { restart = false; }
  void flushParametersStopped() override {
    auto r = rd.load(std::memory_order_relaxed), w = wr.load(std::memory_order_acquire);
    while (r < w) {
      auto &v = pending[r % pending.size()];
      effect->set_parameter(effect, v.index, v.value);
      ++r;
    }
    rd.store(r, std::memory_order_release);
  }
  void process(float **in, float **out, int n, uint64_t) override {
    flushParametersStopped();
    effect->process_float(effect, in, out, n);
  }
  void parameter(uint32_t i, double v) override {
    if (i >= (uint32_t)effect->num_params || !effect->set_parameter)
      throw std::runtime_error("Unknown VST2 parameter");
    auto w = wr.load(std::memory_order_relaxed), r = rd.load(std::memory_order_acquire);
    if (w - r >= pending.size())
      throw std::runtime_error("VST2 parameter queue full");
    pending[w % pending.size()] = {i, (float)std::clamp(v, 0.0, 1.0)};
    wr.store(w + 1, std::memory_order_release);
  }
  Json state() override {
    Json s;
    void *p = nullptr;
    if (effect->flags & (1 << 5)) {
      auto n = call(23, 0, 0, &p);
      if (n <= 0 || n > 64 * 1024 * 1024 || !p)
        throw std::runtime_error("VST2 chunk state failed");
      s["chunk"] = base64(p, n);
    } else {
      Json a = Json::array();
      for (int i = 0; i < effect->num_params; ++i)
        a.push_back(effect->get_parameter(effect, i));
      s["parameters"] = a;
      s["program"] = call(3);
    }
    return s;
  }
  void restore(const Json &s) override {
    if (s.contains("chunk")) {
      auto b = unbase64(s.at("chunk"));
      call(24, 0, b.size(), b.data());
    } else {
      if (s.contains("program"))
        call(2, 0, s["program"].get<int>());
      if (s.contains("parameters")) {
        int i = 0;
        for (auto &v : s["parameters"])
          parameter(i++, v.get<double>());
      }
    }
  }
  Json describe() override {
    char vendor[256]{}, vers[64]{};
    call(47, 0, 0, vendor);
    Json p = Json::array();
    for (int i = 0; i < effect->num_params && i < 65536; ++i) {
      char n[256]{};
      call(8, i, 0, n);
      p.push_back({{"id", i}, {"name", n}, {"defaultValue", effect->get_parameter(effect, i)}});
    }
    return {{"format", "vst2"},
            {"path", path},
            {"classId", std::to_string(effect->unique_id)},
            {"name", name},
            {"vendor", vendor},
            {"version", std::to_string(effect->version)},
            {"inputChannels", effect->num_inputs},
            {"outputChannels", effect->num_outputs},
            {"parameters", p},
            {"latencyFrames", latency()}};
  }
  Json shells() {
    Json all = Json::array();
    if (call(35) == 10) {
      for (int i = 0; i < 4096; ++i) {
        char n[256]{};
        auto id = call(70, 0, 0, n);
        if (!id)
          break;
        all.push_back({{"format", "vst2"},
                       {"path", path},
                       {"classId", std::to_string((int32_t)id)},
                       {"name", n},
                       {"vendor", ""},
                       {"version", std::to_string(effect->version)},
                       {"shell", true}});
      }
    }
    return all;
  }
  void editor(bool show) override {
    if (!show) {
      if (window)
        ShowWindow(window, SW_HIDE);
      return;
    }
    if (!(effect->flags & 1))
      throw std::runtime_error("No VST2 editor");
    if (!window) {
      if (editorAttached) { call(15); editorAttached = false; }
      window = createEditor(name, this);
      if (!window) throw std::runtime_error("Cannot create VST2 editor host window");
      call(14, 0, 0, editorContent);
      editorAttached = true;
      vst_rect_t *r = nullptr;
      call(13, 0, 0, &r);
      if (r)
        sizeEditor(window, r->right - r->left, r->bottom - r->top);
    }
    ShowWindow(window, SW_SHOW);
  }
  void idle() override {
    if (window && IsWindowVisible(window))
      call(19);
  }
};
thread_local int32_t V2::openingId = 0;
std::unique_ptr<Plugin> makePlugin(const Json &j, int channels, double rate, int block) {
  std::unique_ptr<Plugin> p;
  auto f = j.at("format").get<std::string>();
  if (f == "vst3" || f == "VST3")
    p = std::make_unique<V3>(j, channels, rate, block);
  else if (f == "vst2" || f == "VST2")
    p = std::make_unique<V2>(j, channels, rate, block);
  else
    throw std::runtime_error("Unsupported plugin format");
  p->id = j.at("instanceId");
  if (j.contains("parameters"))
    for (auto &[k, v] : j["parameters"].items())
      p->parameter((uint32_t)std::stoul(k), v.get<double>());
  return p;
}
Json scanPlugin(const std::string &path) {
  Json result = Json::array();
  auto ext = std::filesystem::path(wide(path)).extension().wstring();
  std::transform(ext.begin(), ext.end(), ext.begin(), towlower);
  if (ext == L".vst3") {
    std::string err;
    auto m = VST3::Hosting::Module::create(path, err);
    if (!m)
      throw std::runtime_error(err);
    auto f = m->getFactory();
    for (auto &c : f.classInfos())
      if (c.category() == kVstAudioEffectClass)
        result.push_back({{"format", "vst3"},
                          {"path", path},
                          {"classId", c.ID().toString()},
                          {"name", c.name()},
                          {"vendor", c.vendor()},
                          {"version", c.version()}});
  } else {
    V2 p(Json{{"path", path}, {"classId", "0"}}, 2, 48000, 256, true);
    result = p.shells();
    if (result.empty())
      result.push_back(p.describe());
  }
  return result;
}
