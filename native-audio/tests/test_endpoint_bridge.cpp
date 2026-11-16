#include "endpoint_bridge.h"
#include <array>
#include <cstdlib>
#include <iostream>
#include <new>
#include <thread>

static std::atomic<uint64_t> allocations{0};
void *operator new(size_t size) {
  ++allocations;
  if (auto *p = std::malloc(size)) return p;
  throw std::bad_alloc();
}
void operator delete(void *p) noexcept { std::free(p); }
void operator delete(void *p, size_t) noexcept { std::free(p); }
static int checks = 0;
static void require(bool condition, const char *message) {
  if (!condition) throw std::runtime_error(message);
  ++checks;
}
struct EndpointBridgeAudit {
  static void run() {
    EndpointBridge bridge;
    bridge.block = 4; bridge.bridgeFrames = 8; bridge.sampleRate = 48000;
    bridge.inputChannels = 1; bridge.outputChannels = 4;
    bridge.input.resize(4); bridge.output.resize(16);
    unsigned processCalls = 0; bridge.context = &processCalls;
    bridge.process = [](void *counter, float *output, const float *input, unsigned frames,
                        unsigned, int) {
      ++*(unsigned *)counter;
      for (unsigned i = 0; i < frames; ++i)
        for (unsigned c = 0; c < 4; ++c) output[i * 4 + c] = input[i] * (c + 1);
    };
    for (int i = 0; i < 3; ++i) {
      auto p = std::make_unique<EndpointBridge::Port>();
      p->owner = &bridge;
      p->spec = {"test-" + std::to_string(i), "test-same-card", i == 0 ? 1u : 2u,
                 i == 2 ? 2u : 0u, i == 0, i == 1};
      p->ring.prepare(p->spec.channels, 64, i == 0 ? 0 : 8);
      p->audio = std::make_unique<RtAudio>(RtAudio::WINDOWS_WASAPI);
      p->audio->showWarnings(false); // Enumeration only; never openStream/startStream.
      bridge.ports.push_back(std::move(p));
    }
    auto *capture = bridge.ports[0].get(), *clock = bridge.ports[1].get(), *aux = bridge.ports[2].get();
    float in[4]{1,2,3,4}, main[8], secondary[8];
    EndpointBridge::callback(nullptr, in, 4, 0, 0, capture);
    for (auto &x : in) x += 4;
    EndpointBridge::callback(nullptr, in, 4, 0, 0, capture);
    auto before = allocations.load();
    for (int i = 0; i < 3; ++i) {
      for (auto &x : in) x += 4;
      EndpointBridge::callback(nullptr, in, 4, 0, 0, capture);
      EndpointBridge::callback(main, nullptr, 4, 0, 0, clock);
      EndpointBridge::callback(secondary, nullptr, 4, 0, 0, aux);
    }
    require(processCalls == 3, "only primary render invokes the DSP graph");
    require(main[0] == 1 && main[1] == 2 && secondary[0] == 3 && secondary[1] == 4,
            "capture bridge and independent output offsets preserve sample channels");
    require(allocations.load() == before, "no allocation in simulated endpoint callbacks");
    // Consume faster than the designated master: explicit failure, then silence
    // on every endpoint. No implicit frame repeat/drop or rate correction.
    for (int i = 0; i < 3; ++i) EndpointBridge::callback(secondary, nullptr, 4, 0, 0, aux);
    require(bridge.failure() == 4 && aux->underflows == 1, "auxiliary underflow is an essential route fault");
    std::fill_n(main, 8, 99.f);
    EndpointBridge::callback(main, nullptr, 4, 0, 0, clock);
    require(processCalls == 3 && std::all_of(main, main + 8, [](float x) { return x == 0; }),
            "fault silences the primary output without another DSP invocation");
    bridge.fault = 0; bridge.faultPort = -1;
    for (int i = 0; i < 17; ++i) EndpointBridge::callback(nullptr, in, 4, 0, 0, capture);
    require(bridge.failure() == 1 && capture->overflows == 1, "capture overflow is an essential route fault");
  }
};
int main() {
  CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  try {
    EndpointRing ring; ring.prepare(2, 7, 2);
    std::array<float, 12> out; out.fill(99);
    require(ring.pop(out.data(), 2, 3, 1) && out[0] == 99 && out[1] == 0 && out[2] == 0 &&
                out[4] == 0 && out[5] == 0, "prebuffer silence and destination stride");
    std::array<float, 24> input{};
    for (int i = 0; i < 6; ++i) { input[i * 4 + 1] = (float)i + .25f; input[i * 4 + 2] = -(float)i; }
    auto before = allocations.load();
    for (int loop = 0; loop < 1000; ++loop) {
      require(ring.push(input.data(), 6, 4, 1), "strided push across non-power-of-two wrap");
      require(!ring.push(input.data(), 2, 4, 1) && ring.available() == 6, "overflow preserves unread frames");
      require(ring.pop(out.data(), 6, 2), "wrapped pop");
      for (int i = 0; i < 6; ++i)
        require(out[i * 2] == (float)i + .25f && out[i * 2 + 1] == -(float)i, "channel isolation and order");
      require(!ring.pop(out.data(), 1, 2) && ring.available() == 0 && out[0] == .25f,
              "underflow leaves target and counters intact");
    }
    require(allocations.load() == before, "no allocation in prepared ring operations");
    EndpointRing concurrent; concurrent.prepare(2, 257);
    constexpr unsigned frames = 100000;
    std::atomic<bool> ready{false}, exact{true};
    std::thread producer([&] {
      while (!ready.load()) std::this_thread::yield();
      float data[14];
      for (unsigned n = 0; n < frames;) {
        auto count = std::min(7u, frames - n);
        for (unsigned i = 0; i < count; ++i) { data[i * 2] = (float)(n + i); data[i * 2 + 1] = -(float)(n + i); }
        if (concurrent.push(data, count, 2)) n += count;
        else std::this_thread::yield();
      }
    });
    std::thread consumer([&] {
      while (!ready.load()) std::this_thread::yield();
      float data[22];
      for (unsigned n = 0; n < frames;) {
        auto count = std::min(11u, frames - n);
        if (concurrent.pop(data, count, 2)) {
          for (unsigned i = 0; i < count; ++i)
            if (data[i * 2] != (float)(n + i) || data[i * 2 + 1] != -(float)(n + i)) exact = false;
          n += count;
        } else std::this_thread::yield();
      }
    });
    ready = true; producer.join(); consumer.join();
    require(exact && concurrent.available() == 0, "concurrent SPSC preserves 100000 stereo frames");
    EndpointBridgeAudit::run();
    std::cout << "{\"passed\":true,\"assertions\":" << checks
              << ",\"concurrentFrames\":100000,\"preparedRingAllocations\":0}\n";
    CoUninitialize();
    return 0;
  } catch (const std::exception &e) { std::cerr << e.what() << '\n'; CoUninitialize(); return 1; }
}
