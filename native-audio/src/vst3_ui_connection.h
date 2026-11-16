#pragma once
#include "public.sdk/source/vst/hosting/connectionproxy.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include <deque>
#include <mutex>

// Some controllers request analyzer data from their timer thread. The SDK's
// ConnectionProxy discards these requests; deliver them on the owning UI thread.
class Vst3UiConnection final : public Steinberg::Vst::ConnectionProxy {
  std::mutex mutex;
  std::deque<Steinberg::IPtr<Steinberg::Vst::IMessage>> pending;
  bool closed = false;
public:
  explicit Vst3UiConnection(Steinberg::Vst::IConnectionPoint *source)
      : ConnectionProxy(source) {}
  Steinberg::tresult PLUGIN_API notify(Steinberg::Vst::IMessage *message) override {
    if (!message) return Steinberg::kInvalidArgument;
    if (threadChecker && threadChecker->test()) return ConnectionProxy::notify(message);
    std::lock_guard lock(mutex);
    if (closed || pending.size() >= 256) return Steinberg::kResultFalse;
    pending.emplace_back(message);
    return Steinberg::kResultOk;
  }
  void drain() {
    if (!threadChecker || !threadChecker->test()) return;
    decltype(pending) ready;
    { std::lock_guard lock(mutex); ready.swap(pending); }
    for (auto &message : ready) ConnectionProxy::notify(message);
  }
  Steinberg::tresult PLUGIN_API disconnect(Steinberg::Vst::IConnectionPoint *other) override {
    decltype(pending) discarded;
    { std::lock_guard lock(mutex); closed = true; discarded.swap(pending); }
    return ConnectionProxy::disconnect(other);
  }
};

class Vst3UiPlugProvider final : public Steinberg::Vst::PlugProvider {
public:
  using PlugProvider::PlugProvider;
  void drain() {
    if (!componentCP || !controllerCP) return;
    static_cast<Vst3UiConnection *>(componentCP.get())->drain();
    static_cast<Vst3UiConnection *>(controllerCP.get())->drain();
  }
};