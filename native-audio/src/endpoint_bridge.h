#pragma once
#include "RtAudio.h"
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

// One producer and one consumer. Storage and initial silence are prepared while
// stopped. A single consumer owns the resampling phase as well as the read cursor.
class EndpointRing {
  static_assert(std::atomic<uint64_t>::is_always_lock_free,
                "Endpoint bridge counters must be lock-free on the target");
  std::vector<float> samples;
  unsigned channels = 0, capacity = 0;
  alignas(64) std::atomic<uint64_t> written{0};
  alignas(64) std::atomic<uint64_t> read{0};
  static constexpr unsigned taps = 32, phases = 256;
  std::vector<float> coefficients;
  double nominalRatio = 1, fraction = 0, correction = 0;
  unsigned targetFill = 0, destinationRate = 0;
  bool primed = false;
public:
  std::atomic<int> driftPpm{0};
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
  // The ring contains source-clock frames. Build the fractional-delay low-pass
  // filter off the callback thread, including the cutoff for downsampling.
  void prepareAdaptive(unsigned ch, unsigned sourceRate, unsigned destRate, unsigned target) {
    targetFill = std::max(target, taps * 2);
    prepare(ch, targetFill * 4, 0);
    nominalRatio = double(sourceRate) / destRate;
    destinationRate = destRate;
    correction = fraction = 0;
    driftPpm = 0;
    primed = false;
    const double cutoff = 0.94 * std::min(1.0, 1.0 / nominalRatio);
    constexpr double pi = 3.141592653589793;
    coefficients.resize(taps * phases);
    for (unsigned phase = 0; phase < phases; ++phase) {
      auto *weights = coefficients.data() + phase * taps;
      double sum = 0;
      for (unsigned k = 0; k < taps; ++k) {
        double x = double(k) - (taps / 2 - 1) - double(phase) / phases;
        double window = 0.42 + 0.5 * std::cos(pi * x / (taps / 2)) +
                        0.08 * std::cos(2 * pi * x / (taps / 2));
        weights[k] = float(window * (std::abs(x) < 1e-12 ? cutoff : std::sin(pi * cutoff * x) / (pi * x)));
        sum += weights[k];
      }
      for (unsigned k = 0; k < taps; ++k) weights[k] /= float(sum);
    }
  }
  bool popAdaptive(float *target, unsigned frames, unsigned stride, unsigned offset,
                   bool &underrun) {
    underrun = false;
    auto r = read.load(std::memory_order_relaxed);
    auto queued = written.load(std::memory_order_acquire) - r;
    if (!primed && queued < targetFill) return false;
    // A paused consumer resumes near the present instead of replaying old audio.
    if (queued > targetFill * 2) {
      r += queued - targetFill;
      queued = targetFill;
      read.store(r, std::memory_order_release);
      fraction = 0;
    }
    primed = true;
    // Queue error adjusts the consumption rate by at most 0.5 percent. Smooth
    // over one second to follow clock drift without chasing packet jitter.
    const double requested = std::clamp((double(queued) - targetFill) / targetFill * .02, -.005, .005);
    correction += (requested - correction) * std::min(1.0, double(frames) / destinationRate);
    driftPpm.store(int(std::lround(correction * 1e6)), std::memory_order_relaxed);
    const double step = nominalRatio * (1 + correction);
    if (queued < std::max(uint64_t(fraction + frames * step),
                          uint64_t(fraction + (frames - 1) * step) + taps)) {
      underrun = true;
      primed = false;
      return false;
    }
    double position = fraction;
    for (unsigned i = 0; i < frames; ++i, position += step) {
      const auto whole = uint64_t(position);
      const auto phase = unsigned((position - whole) * phases);
      const float *weights = coefficients.data() + phase * taps;
      float *dest = target + size_t(i) * stride + offset;
      std::fill_n(dest, channels, 0.f);
      unsigned index = unsigned((r + whole) % capacity);
      for (unsigned k = 0; k < taps; ++k) {
        const float *frame = samples.data() + size_t(index) * channels;
        for (unsigned c = 0; c < channels; ++c) dest[c] += frame[c] * weights[k];
        if (++index == capacity) index = 0;
      }
    }
    const auto consumed = uint64_t(position);
    fraction = position - consumed;
    read.store(r + consumed, std::memory_order_release);
    return true;
  }
};

struct EndpointSpec {
  std::string deviceId, groupId;
  unsigned channels = 0, offset = 0;
  bool input = false, clock = false;
  bool asio = false;
  unsigned nativeRate = 0;
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
    std::atomic<int> fault{0};
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
  bool started = false, adaptive = false;
  EndpointSpec clockOutput, clockInput;
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
      if (nanos > (double)frames / (port.spec.nativeRate ? port.spec.nativeRate : port.owner->sampleRate) * 1e9) ++port.deadlineMisses;
      auto previous = port.maxCallbackNanos.load();
      while (nanos > previous && !port.maxCallbackNanos.compare_exchange_weak(previous, nanos)) {}
    }
  };
  void fail(Port &port, int code) {
    port.fault.store(code, std::memory_order_release);
    if (adaptive && !port.spec.clock) return;
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
    if (b.fault.load() || p.fault.load()) return 0;
    if (frames > (b.adaptive && !p.spec.clock ? p.actualBlock : b.block) || (p.spec.input && !in) || (!p.spec.input && !out)) {
      b.fail(p, 6); return 0;
    }
    if (b.adaptive) {
      if (p.spec.input) {
        if (!p.ring.push((const float *)in, frames, p.spec.channels)) ++p.overflows;
      } else {
        p.deviceQueue = p.audio->getOutputQueueFrames();
        if (p.spec.clock)
          b.processAdaptive((float *)out, nullptr, frames, status, int(p.deviceQueue.load()));
        else
          b.readAux(p, (float *)out, frames, p.spec.channels, 0);
      }
      return 0;
    }
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
  void readAux(Port &p, float *target, unsigned frames, unsigned stride, unsigned offset) {
    bool underrun;
    if (!p.ring.popAdaptive(target, frames, stride, offset, underrun)) {
      if (underrun) ++p.underflows;
      else ++p.warmupCallbacks;
    }
  }
  void processAdaptive(float *out, const float *in, unsigned frames, unsigned status, int latency) {
    std::fill_n(input.data(), size_t(frames) * inputChannels, 0.f);
    if (in && clockInput.channels)
      for (unsigned i = 0; i < frames; ++i)
        std::copy_n(in + size_t(i) * clockInput.channels, clockInput.channels,
                    input.data() + size_t(i) * inputChannels + clockInput.offset);
    for (auto &p : ports) if (p->spec.input && !p->fault.load())
      readAux(*p, input.data(), frames, inputChannels, p->spec.offset);
    process(context, output.data(), input.data(), frames, status, latency);
    for (auto &p : ports) if (!p->spec.input && !p->spec.clock && !p->fault.load())
      if (!p->ring.push(output.data(), frames, outputChannels, p->spec.offset)) ++p->overflows;
    for (unsigned i = 0; i < frames; ++i)
      std::copy_n(output.data() + size_t(i) * outputChannels + clockOutput.offset,
                  clockOutput.channels, out + size_t(i) * clockOutput.channels);
  }
public:
  static void asioClock(void *context, float *out, const float *in, unsigned frames,
                        unsigned status, int latency) {
    static_cast<EndpointBridge *>(context)->processAdaptive(out, in, frames, status, latency);
  }
  unsigned asioInputs() const { return clockInput.channels; }
  unsigned asioOutputs() const { return clockOutput.channels; }
  ~EndpointBridge() { stop(); }
  const auto &endpoints() const { return ports; }
  unsigned buffering() const { return bridgeFrames; }
  int failure() const { return fault.load(); }
  static const char *faultLabel(int code) {
    static const char *labels[] = {"", "capture bridge overflow", "capture bridge underflow",
      "output bridge overflow", "output bridge underflow", "WASAPI endpoint failure",
      "unexpected endpoint callback shape"};
    return labels[code];
  }
  std::string failureMessage() const {
    auto code = fault.load(), ix = faultPort.load();
    if (!code) return "";
    return std::string(faultLabel(code)) + " at " +
        (ix >= 0 ? ports[(size_t)ix]->spec.deviceId : "unknown endpoint") +
        "; rack stopped.";
  }
  int latency() const {
    for (auto &p : ports) if (p->spec.clock)
      return (int)(p->ring.available() + p->deviceQueue.load());
    return 0;
  }
  bool allRunning() const {
    return started && !fault.load() && std::all_of(ports.begin(), ports.end(),
        [this](auto &p) { return (adaptive && !p->spec.clock) || (p->audio && p->audio->isStreamRunning()); });
  }
  void checkHealth() {
    if (!started) return;
    const auto now = GetTickCount64();
    for (auto &p : ports)
      if (!p->fault.load() && (!p->audio || !p->audio->isStreamRunning() ||
          (now - startedAt > 2000 && now - p->lastCallbackMs.load() > 1000))) {
        fail(*p, 5);
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
private:
  void openPort(Port &p) {
    auto &s = p.spec;
    p.audio = std::make_unique<RtAudio>(RtAudio::WINDOWS_WASAPI);
    p.audio->showWarnings(false);
    unsigned device = 0;
    for (auto id : p.audio->getDeviceIds()) {
      auto d = p.audio->getDeviceInfo(id);
      if (d.stableId != s.deviceId) continue;
      if ((!adaptive && (d.groupId.empty() || d.groupId != s.groupId)) ||
          ((!adaptive || s.clock) && d.preferredSampleRate != sampleRate) ||
          !d.preferredSampleRate || (s.input ? d.inputChannels : d.outputChannels) < s.channels)
        throw std::runtime_error("Endpoint group, mix rate or channel bounds changed before start");
      device = id;
      s.nativeRate = d.preferredSampleRate;
      break;
    }
    if (!device) throw std::runtime_error("Configured WASAPI endpoint disappeared before start");
    p.audio->setErrorCallback([this, &p](RtAudioErrorType type, const std::string &) {
      if (type != RTAUDIO_WARNING && type != RTAUDIO_NO_ERROR) fail(p, 5);
    });
    p.actualBlock = block;
    if (adaptive && !s.clock) {
      // WASAPI packet sizes are independent of the potentially tiny ASIO block.
      p.actualBlock = std::max(s.nativeRate / 200, unsigned(std::ceil(double(block) * s.nativeRate / sampleRate)));
      const unsigned sourceRate = s.input ? s.nativeRate : sampleRate;
      p.ring.prepareAdaptive(s.channels, sourceRate, s.input ? sampleRate : s.nativeRate,
                             unsigned(std::ceil(double(bridgeFrames) * sourceRate / sampleRate)));
    } else if (!adaptive) {
      p.ring.prepare(s.channels, std::max(sampleRate / 2, block * 16), s.input ? 0 : bridgeFrames);
    }
    RtAudio::StreamParameters params;
    params.deviceId = device;
    params.nChannels = s.channels;
    RtAudio::StreamOptions options;
    options.streamName = "Yideck Audio Rack";
    auto result = p.audio->openStream(s.input ? nullptr : &params, s.input ? &params : nullptr,
        RTAUDIO_FLOAT32, s.nativeRate, &p.actualBlock, callback, &p, &options);
    if (result != RTAUDIO_NO_ERROR) throw std::runtime_error("WASAPI endpoint open failed: " + p.audio->getErrorText());
    if (!p.actualBlock || ((!adaptive || s.clock) && p.actualBlock > block))
      throw std::runtime_error("Endpoint needs a larger block than prepared graph");
  }
public:
  void open(const std::vector<EndpointSpec> &specs, unsigned rate, unsigned frames,
            Process fn, void *ctx, bool independentClocks = false) {
    stop(); ports.clear(); fault = 0; faultPort = -1;
    adaptive = independentClocks;
    block = frames; process = fn; context = ctx;
    sampleRate = rate; primaryFrames = 0;
    inputChannels = outputChannels = 0;
    clockInput = {}; clockOutput = {};
    bridgeFrames = std::max(adaptive ? rate / 25 : 1024u, frames * 4);
    for (auto &s : specs) {
      (s.input ? inputChannels : outputChannels) += s.channels;
      if (s.clock) clockOutput = s;
      if (s.asio && s.input) clockInput = s;
    }
    input.assign((size_t)frames * inputChannels, 0);
    output.assign((size_t)frames * outputChannels, 0);
    try {
      for (auto &s : specs) {
        if (s.asio) continue;
        ports.push_back(std::make_unique<Port>());
        auto &p = *ports.back();
        p.spec = s; p.owner = this;
        try {
          openPort(p);
        } catch (const std::runtime_error &) {
          if (!adaptive || s.clock) throw;
          fail(p, 5);
        }
      }
    } catch (...) { stop(); throw; }
  }
  void start() {
    try {
      for (int phase = 0; phase < 3; ++phase)
        for (auto &p : ports) {
          if ((p->spec.input ? 0 : p->spec.clock ? 2 : 1) != phase || p->fault.load()) continue;
          if (p->audio->startStream() != RTAUDIO_NO_ERROR) {
            fail(*p, 5);
            if (!adaptive || p->spec.clock)
              throw std::runtime_error("WASAPI endpoint start failed: " + p->audio->getErrorText());
          }
        }
      if (fault.load()) throw std::runtime_error(failureMessage());
      startedAt = GetTickCount64(); started = true;
    } catch (...) { stop(); throw; }
  }
};
