#pragma once
#include "RtAudio.h"
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

// One producer and one consumer. Storage and initial silence are prepared while
// stopped. No callback allocation, locks, resizing, sample-rate conversion or
// hidden frame dropping. Monotonic counters also make wraparound unambiguous.
class EndpointRing {
  static_assert(std::atomic<uint64_t>::is_always_lock_free,
                "Endpoint bridge counters must be lock-free on the target");
  std::vector<float> samples;
  unsigned channels = 0, capacity = 0;
  alignas(64) std::atomic<uint64_t> written{0};
  alignas(64) std::atomic<uint64_t> read{0};
public:
  void prepare(unsigned ch, unsigned cap, unsigned silence = 0) {
    if (!ch || !cap || silence > cap) throw std::runtime_error("Invalid endpoint ring shape");
    channels = ch; capacity = cap;
    samples.assign((size_t)ch * cap, 0);
    read = 0; written = silence;
  }
  uint64_t available() const {
    auto r = read.load(std::memory_order_acquire), w = written.load(std::memory_order_acquire);
    return w >= r ? w - r : 0;
  }
  bool push(const float *source, unsigned frames, unsigned stride, unsigned offset = 0) {
    auto w = written.load(std::memory_order_relaxed), r = read.load(std::memory_order_acquire);
    if (w < r || w - r + frames > capacity) return false;
    for (unsigned i = 0; i < frames; ++i)
      for (unsigned c = 0; c < channels; ++c)
        samples[((w + i) % capacity) * channels + c] = source[(size_t)i * stride + offset + c];
    written.store(w + frames, std::memory_order_release);
    return true;
  }
  bool pop(float *target, unsigned frames, unsigned stride, unsigned offset = 0) {
    auto r = read.load(std::memory_order_relaxed), w = written.load(std::memory_order_acquire);
    if (w < r || w - r < frames) return false;
    for (unsigned i = 0; i < frames; ++i)
      for (unsigned c = 0; c < channels; ++c)
        target[(size_t)i * stride + offset + c] = samples[((r + i) % capacity) * channels + c];
    read.store(r + frames, std::memory_order_release);
    return true;
  }
};

struct EndpointSpec {
  std::string deviceId, groupId;
  unsigned channels = 0, offset = 0;
  bool input = false, clock = false;
};

// Only the designated output callback invokes Process. Auxiliary callbacks move
// samples through bounded rings; all plugins run once in the primary graph.
class EndpointBridge {
  friend struct EndpointBridgeAudit;
public:
  using Process = void (*)(void *, float *, const float *, unsigned, unsigned, int);
  struct Port {
    EndpointSpec spec;
    EndpointBridge *owner = nullptr;
    std::unique_ptr<RtAudio> audio;
    EndpointRing ring;
    unsigned actualBlock = 0;
    bool capturePrimed = false;
    std::atomic<uint64_t> callbacks{0}, frames{0}, underflows{0}, overflows{0}, xruns{0},
        warmupCallbacks{0};
    std::atomic<unsigned> deviceQueue{0};
    std::atomic<uint64_t> lastCallbackMs{0};
    std::atomic<uint64_t> callbackNanos{0}, maxCallbackNanos{0}, deadlineMisses{0};
  };
private:
  std::vector<std::unique_ptr<Port>> ports;
  std::vector<float> input, output;
  unsigned inputChannels = 0, outputChannels = 0, block = 0, bridgeFrames = 0;
  Process process = nullptr;
  void *context = nullptr;
  std::atomic<bool> ready{false};
  std::atomic<int> fault{0}; // 1 capture overflow, 2 capture underflow, 3 output overflow,
                           // 4 output underflow, 5 backend error, 6 unexpected callback shape.
  std::atomic<int> faultPort{-1};
  bool started = false;
  uint64_t startedAt = 0;
  unsigned sampleRate = 0;
  uint64_t primaryFrames = 0;
  struct CallbackTiming {
    Port &port;
    unsigned frames;
    std::chrono::steady_clock::time_point begin = std::chrono::steady_clock::now();
    ~CallbackTiming() {
      auto nanos = (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
          std::chrono::steady_clock::now() - begin).count();
      port.callbackNanos += nanos;
      if (nanos > (double)frames / port.owner->sampleRate * 1e9) ++port.deadlineMisses;
      auto previous = port.maxCallbackNanos.load();
      while (nanos > previous && !port.maxCallbackNanos.compare_exchange_weak(previous, nanos)) {}
    }
  };
  void fail(Port &port, int code) {
    int expected = 0;
    if (fault.compare_exchange_strong(expected, code))
      for (size_t i = 0; i < ports.size(); ++i)
        if (ports[i].get() == &port) { faultPort.store((int)i); break; }
  }
  static int callback(void *out, void *in, unsigned frames, double, RtAudioStreamStatus status,
                      void *context) {
    auto &p = *(Port *)context; auto &b = *p.owner;
    CallbackTiming timing{p, frames};
    ++p.callbacks; p.frames += frames;
    p.lastCallbackMs = GetTickCount64();
    if (status) ++p.xruns;
    if (out) std::fill_n((float *)out, (size_t)frames * p.spec.channels, 0.f);
    if (frames > b.block || (p.spec.input && !in) || (!p.spec.input && !out)) {
      b.fail(p, 6); return 0;
    }
    if (b.fault.load()) return 0;
    if (p.spec.input) {
      // Capture starts before the primary output, providing a bounded warmup.
      if (!p.ring.push((float *)in, frames, p.spec.channels)) {
        ++p.overflows; b.fail(p, 1);
      }
      return 0;
    }
    p.deviceQueue = p.audio->getOutputQueueFrames();
    if (!p.spec.clock) {
      if (!b.ready.load(std::memory_order_acquire)) { ++p.warmupCallbacks; return 0; }
      if (!p.ring.pop((float *)out, frames, p.spec.channels)) {
        ++p.underflows; b.fail(p, 4);
      }
      return 0;
    }
    std::fill_n(b.input.data(), (size_t)frames * b.inputChannels, 0.f);
    b.primaryFrames += frames;
    for (auto &source : b.ports) if (source->spec.input) {
      if (!source->capturePrimed) {
        source->capturePrimed = source->ring.available() >= b.bridgeFrames;
        if (!source->capturePrimed) {
          ++source->warmupCallbacks;
          if (b.primaryFrames > b.sampleRate / 2) b.fail(*source, 2);
          continue;
        }
      }
      if (!source->ring.pop(b.input.data(), frames, b.inputChannels, source->spec.offset)) {
        ++source->underflows; b.fail(*source, 2);
      }
    }
    if (b.fault.load()) return 0;
    b.process(b.context, b.output.data(), b.input.data(), frames, status, b.latency());
    for (auto &destination : b.ports) if (!destination->spec.input) {
      if (!destination->ring.push(b.output.data(), frames, b.outputChannels,
                                  destination->spec.offset)) {
        ++destination->overflows; b.fail(*destination, 3);
      }
    }
    if (b.fault.load()) return 0;
    b.ready.store(true, std::memory_order_release);
    if (!p.ring.pop((float *)out, frames, p.spec.channels)) {
      ++p.underflows; b.fail(p, 4);
    }
    return 0;
  }
public:
  ~EndpointBridge() { stop(); }
  const auto &endpoints() const { return ports; }
  unsigned buffering() const { return bridgeFrames; }
  int failure() const { return fault.load(); }
  std::string failureMessage() const {
    static const char *labels[] = {"", "capture bridge overflow", "capture bridge underflow",
      "output bridge overflow", "output bridge underflow", "WASAPI endpoint failure",
      "unexpected endpoint callback shape"};
    auto code = fault.load(), ix = faultPort.load();
    if (!code) return "";
    return std::string(labels[code]) + " at " +
        (ix >= 0 ? ports[(size_t)ix]->spec.deviceId : "unknown endpoint") +
        "; all rack endpoints stopped. No frames were silently dropped or resampled.";
  }
  int latency() const {
    for (auto &p : ports) if (p->spec.clock)
      return (int)(p->ring.available() + p->deviceQueue.load());
    return 0;
  }
  bool allRunning() const {
    return started && !fault.load() && std::all_of(ports.begin(), ports.end(),
        [](auto &p) { return p->audio && p->audio->isStreamRunning(); });
  }
  void checkHealth() {
    if (!started) return;
    const auto now = GetTickCount64();
    for (auto &p : ports)
      if (!p->audio->isStreamRunning() ||
          (now - startedAt > 2000 && now - p->lastCallbackMs.load() > 1000)) {
        fail(*p, 5); break;
      }
  }
  void stop() {
    ready = false; started = false;
    // Quiesce the sole DSP producer first, then its consumers/capture producers.
    for (int phase = 0; phase < 2; ++phase)
      for (auto &p : ports) if (p->spec.clock == (phase == 0) && p->audio) {
        if (p->audio->isStreamRunning()) p->audio->abortStream();
        if (p->audio->isStreamOpen()) p->audio->closeStream();
      }
  }
  void open(const std::vector<EndpointSpec> &specs, unsigned rate, unsigned frames,
            Process fn, void *ctx) {
    stop(); ports.clear(); fault = 0; faultPort = -1;
    block = frames; process = fn; context = ctx;
    sampleRate = rate; primaryFrames = 0;
    inputChannels = outputChannels = 0;
    bridgeFrames = std::max(1024u, frames * 4);
    for (auto &s : specs) (s.input ? inputChannels : outputChannels) += s.channels;
    input.assign((size_t)frames * inputChannels, 0);
    output.assign((size_t)frames * outputChannels, 0);
    try {
      for (auto &s : specs) {
        auto p = std::make_unique<Port>(); p->spec = s; p->owner = this;
        p->ring.prepare(s.channels, std::max(rate / 2, frames * 16), s.input ? 0 : bridgeFrames);
        p->audio = std::make_unique<RtAudio>(RtAudio::WINDOWS_WASAPI);
        p->audio->showWarnings(false);
        auto *raw = p.get();
        p->audio->setErrorCallback([this, raw](RtAudioErrorType type, const std::string &) {
          if (type != RTAUDIO_WARNING && type != RTAUDIO_NO_ERROR) fail(*raw, 5);
        });
        unsigned device = 0; bool found = false;
        for (auto id : p->audio->getDeviceIds()) {
          auto d = p->audio->getDeviceInfo(id);
          if (d.stableId != s.deviceId) continue;
          if (d.groupId.empty() || d.groupId != s.groupId || d.preferredSampleRate != rate ||
              (s.input ? d.inputChannels : d.outputChannels) < s.channels)
            throw std::runtime_error("Endpoint group, mix rate or channel bounds changed before start");
          device = id; found = true; break;
        }
        if (!found) throw std::runtime_error("Configured WASAPI endpoint disappeared before start");
        RtAudio::StreamParameters parameters; parameters.deviceId = device;
        parameters.nChannels = s.channels; parameters.firstChannel = 0;
        RtAudio::StreamOptions options; options.streamName = "Yideck Audio Rack";
        p->actualBlock = frames;
        auto result = p->audio->openStream(s.input ? nullptr : &parameters,
            s.input ? &parameters : nullptr, RTAUDIO_FLOAT32, rate, &p->actualBlock,
            callback, p.get(), &options);
        if (result != RTAUDIO_NO_ERROR)
          throw std::runtime_error("WASAPI endpoint open failed: " + p->audio->getErrorText());
        if (!p->actualBlock || p->actualBlock > frames)
          throw std::runtime_error("Endpoint needs a larger block than prepared graph");
        ports.push_back(std::move(p));
      }
    } catch (...) { stop(); throw; }
  }
  void start() {
    try {
      // Capture, auxiliary renders, and finally the primary render clock.
      for (int phase = 0; phase < 3; ++phase)
        for (auto &p : ports)
          if ((p->spec.input ? 0 : p->spec.clock ? 2 : 1) == phase) {
            if (p->audio->startStream() != RTAUDIO_NO_ERROR)
              throw std::runtime_error("WASAPI endpoint start failed: " + p->audio->getErrorText());
          }
      if (fault.load()) throw std::runtime_error(failureMessage());
      startedAt = GetTickCount64(); started = true;
    } catch (...) { stop(); throw; }
  }
};
