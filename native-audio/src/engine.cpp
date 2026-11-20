#include "RtAudio.h"
#include "plugins.h"
#include "device_policy.h"
#include "endpoint_bridge.h"
#include "protocol.h"
#include "interval_meter.h"
#include "output_signal.h"
#ifdef YIDECK_ENABLE_ASIO
#include "asio_backend.h"
#endif
#include <array>
#include <chrono>
#include <cmath>
#include <deque>
#include <iostream>
#include <map>
#include <mutex>
#include <numeric>
#include <set>
#include <thread>
static constexpr int MaxBlock = 8192;
struct Delay {
  struct History { std::vector<float> data; int pos = 0; };
  std::shared_ptr<History> history = std::make_shared<History>();
  int frames = 0, channels = 0;
  void setup(int n, int ch) {
    if (n < 0 || n > 480000)
      throw std::runtime_error("PDC latency outside supported range");
    frames = n;
    channels = ch;
    history = std::make_shared<History>();
    history->data.assign((size_t)n * ch, 0);
  }
  float step(float x, int channel, int offset) {
    if (!frames)
      return x;
    int p = (history->pos + offset) % frames;
    auto &v = history->data[(size_t)channel * frames + p];
    float y = v;
    v = x;
    return y;
  }
  void advance(int n) {
    if (frames)
      history->pos = (history->pos + n) % frames;
  }
  void retain(const Delay &old) {
    if (frames == old.frames && channels == old.channels) history = old.history;
  }
};
struct Media {
  HANDLE handle = nullptr;
  unsigned char *view = nullptr;
  uint32_t capacity = 0;
  std::atomic<uint64_t> consumed{0}, generation{0}, underruns{0};
  std::atomic<int> latency{0};
  ~Media() { detach(); }
  void detach() {
    if (view)
      UnmapViewOfFile(view);
    if (handle)
      CloseHandle(handle);
    view = nullptr;
    handle = nullptr;
  }
  uint64_t load(int off) {
    return (uint64_t)InterlockedCompareExchange64((volatile LONG64 *)(view + off), 0, 0);
  }
  void put(int off, uint64_t value) {
    InterlockedExchange64((volatile LONG64 *)(view + off), (LONG64)value);
  }
  void attach(const std::string &name, unsigned rate) {
    detach();
    handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, wide(name).c_str());
    if (!handle)
      throw std::runtime_error("Media mapping not found");
    view = (unsigned char *)MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, 128);
    if (!view)
      throw std::runtime_error("Media header mapping failed");
    auto *u = (uint32_t *)view;
    capacity = u[2];
    if (u[0] != 0x59415231 || u[1] != 1 || capacity < 4096 || capacity > 1048576 || u[3] != 2 ||
        u[4] != rate) {
      detach();
      throw std::runtime_error("Media ring sample rate must match configured device rate");
    }
    UnmapViewOfFile(view);
    view = (unsigned char *)MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0,
                                          128 + (size_t)capacity * 8);
    if (!view) {
      detach();
      throw std::runtime_error("Media PCM mapping failed");
    }
    generation = load(40);
    consumed = 0;
    underruns = 0;
  }
  void reset(uint64_t gen) {
    if (!view)
      throw std::runtime_error("Media mapping not attached");
    put(32, load(24));
    generation = gen;
    consumed = underruns = 0;
    put(40, gen);
    publish();
  }
  void publish() {
    auto c = consumed.load();
    auto l = (uint64_t)latency.load();
    put(48, c);
    put(56, c > l ? c - l : 0);
    put(64, underruns.load());
  }
  void read(float **out, int frames) {
    for (int c = 0; c < 2; ++c)
      std::fill_n(out[c], frames, 0.f);
    if (!view)
      return;
    auto r = load(32), w = load(24);
    if (w < r || w - r > capacity) {
      ++underruns;
      publish();
      return;
    }
    int n = (int)std::min<uint64_t>(frames, w - r);
    auto *p = (float *)(view + 128);
    for (int i = 0; i < n; ++i)
      for (int c = 0; c < 2; ++c)
        out[c][i] = p[((r + i) % capacity) * 2 + c];
    put(32, r + n);
    consumed += n;
    if (n < frames)
      ++underruns;
    publish();
  }
};
struct Insert {
  std::shared_ptr<Plugin> plugin;
  std::string identity;
  bool bypass = false;
  float wet = 1;
  float desiredWet = 1;
  bool desiredBypass = false;
  Delay dry;
};
struct Node {
  std::string id, kind;
  int channels = 2, latency = 0, pluginLatencySamples = 0;
  float gain = 1, pan = 0;
  struct GainHistory { float left = 1, right = 1; };
  std::shared_ptr<GainHistory> gainHistory = std::make_shared<GainHistory>();
  bool mute = false, monitor = true;
  float desiredGain = 1, desiredPan = 0;
  bool desiredMute = false, desiredMonitor = true;
  std::vector<Insert> inserts;
  std::array<std::vector<float>, 2> pre, post, scratch;
  std::shared_ptr<IntervalMeter> meter = std::make_shared<IntervalMeter>();
  Delay outputDelay;
};
struct Edge {
  std::string id;
  int from, to, fromCh = 0, toCh = 0, channels = 2;
  bool pre = false, monoExpand = false;
  bool main = false;
  float gain = 1;
  float desiredGain = 1;
  bool desiredPre = false;
  Delay delay;
};
struct Graph {
  using ChannelRoutes = std::array<std::vector<int>, 2>;
  using HardwareRoutes = std::vector<ChannelRoutes>;
  std::unique_ptr<HardwareRoutes> routes = std::make_unique<HardwareRoutes>();
  std::atomic<const HardwareRoutes *> activeRoutes{nullptr};
  static ChannelRoutes readRoutes(const Json &node, int width) {
    if (node.contains("flatRoutes")) return node.at("flatRoutes").get<ChannelRoutes>();
    auto channels = node.value("deviceChannels", std::vector<int>{0, 1});
    if (channels.size() < (size_t)width) throw std::runtime_error("Missing channel mapping");
    for (int c : channels)
      if (c < 0 || c >= 256) throw std::runtime_error("Invalid physical channel");
    ChannelRoutes result;
    for (int c = 0; c < width; ++c) result[c].push_back(channels[c]);
    return result;
  }
  std::vector<Node> nodes;
  std::vector<Edge> edges;
  std::vector<int> order;
  int latency = 0, block;
  double rate;
  std::array<std::vector<float>, 2> mediaBuffer;
  struct Control {
    int kind = 0, index = 0, insertIndex = 0;
    float gain = 1, pan = 0, wet = 1;
    bool mute = false, monitor = true, pre = false, bypass = false;
  };
  std::array<Control, 8192> controlQueue{};
  std::atomic<uint32_t> controlWrite{0}, controlRead{0};
  Graph(const Json &j, int bs, double sr, Graph *previous = nullptr) : block(bs), rate(sr) {
    if (!j.contains("nodes") || j["nodes"].size() > 256 ||
        j.value("edges", Json::array()).size() > 4096)
      throw std::runtime_error("Invalid graph size");
    std::map<std::string, int> ids;
    std::set<std::string> pluginIds;
    for (auto &n : j["nodes"]) {
      Node a;
      a.id = n.at("id");
      a.kind = n.at("kind");
      a.channels = n.value("channels", 2);
      if (a.id.empty() || ids.contains(a.id) || a.channels < 1 || a.channels > 2)
        throw std::runtime_error("Invalid node identity/channels");
      if (a.kind != "input" && a.kind != "media" && a.kind != "bus" && a.kind != "fx" &&
          a.kind != "output")
        throw std::runtime_error("Unknown node kind");
      if (previous)
        for (auto &old : previous->nodes)
          if (old.id == a.id && old.channels == a.channels) a.meter = old.meter;
      a.gain = a.desiredGain = n.value("gain", 1.f);
      a.pan = a.desiredPan = n.value("pan", 0.f);
      a.mute = a.desiredMute = n.value("mute", false);
      a.monitor = a.desiredMonitor = n.value("monitor", true);
      validateControl(a.gain, a.pan, true);
      // A newly prepared muted route must never leak a first-block fade from unity.
      // Only subsequent live control changes ramp from the previous actual gain.
      a.gainHistory->left = a.mute ? 0 : a.gain * (a.channels > 1 && a.pan > 0 ? 1 - a.pan : 1);
      a.gainHistory->right = a.mute ? 0 : a.gain * (a.pan < 0 ? 1 + a.pan : 1);
      if (previous) for (const auto &old : previous->nodes)
        if (old.id == a.id && old.channels == a.channels && old.kind == a.kind &&
            old.desiredGain == a.desiredGain && old.desiredPan == a.desiredPan && old.desiredMute == a.desiredMute)
          a.gainHistory = old.gainHistory;
      routes->push_back(readRoutes(n, a.channels));
      for (int c = 0; c < 2; ++c) {
        a.pre[c].resize(bs);
        a.post[c].resize(bs);
        a.scratch[c].resize(bs);
      }
      for (auto &p : n.value("plugins", Json::array())) {
        Insert x;
        auto id = p.at("instanceId").get<std::string>();
        if (id.empty() || !pluginIds.insert(id).second)
          throw std::runtime_error("Duplicate plugin instance identity");
        x.identity =
            Json::array({p.at("format"), p.at("path"), p.value("classId", ""), a.channels}).dump();
        if (previous)
          for (auto &old : previous->nodes)
            for (auto &i : old.inserts)
              if (i.plugin->id == id && i.identity == x.identity)
                x.plugin = i.plugin;
        if (!x.plugin) {
          try { x.plugin = makePlugin(p, a.channels, sr, bs); }
          catch (const std::exception &e) { throw PluginFailure(id, e.what()); }
        }
        x.wet = x.desiredWet = p.value("wet", 1.f);
        x.bypass = x.desiredBypass = p.value("bypass", false);
        if (!std::isfinite(x.wet) || x.wet < 0 || x.wet > 1)
          throw std::runtime_error("Invalid plugin wet");
        x.dry.setup(x.plugin->latency(), a.channels);
        a.inserts.push_back(std::move(x));
      }
      ids[a.id] = (int)nodes.size();
      nodes.push_back(std::move(a));
    }
    activeRoutes.store(routes.get());
    for (auto &e : j.value("edges", Json::array())) {
      Edge a;
      a.id = e.value("id", std::string("legacy-") + std::to_string(edges.size()));
      if (std::any_of(edges.begin(), edges.end(), [&](auto &old) { return old.id == a.id; }))
        throw std::runtime_error("Duplicate edge id");
      auto f = e.at("from").get<std::string>(), t = e.at("to").get<std::string>();
      a.main = a.id == f + ":main";
      if (!ids.contains(f) || !ids.contains(t))
        throw std::runtime_error("Missing edge endpoint");
      a.from = ids[f];
      a.to = ids[t];
      auto tap = e.value("source", "post");
      if (tap != "pre" && tap != "post") throw std::runtime_error("Invalid route source tap");
      a.pre = a.desiredPre = tap == "pre";
      a.gain = a.desiredGain = e.value("gain", 1.f);
      validateControl(a.gain, 0, a.main);
      a.fromCh = e.value("fromChannel", 0);
      a.toCh = e.value("toChannel", 0);
      a.channels = e.value("channels", std::min(nodes[a.from].channels, nodes[a.to].channels));
      if (!std::isfinite(a.gain) || a.gain < 0 || a.gain > 16 || a.channels < 1 || a.fromCh < 0 ||
          a.toCh < 0 || a.fromCh + a.channels > nodes[a.from].channels ||
          a.toCh + a.channels > nodes[a.to].channels)
        throw std::runtime_error("Invalid edge channels/gain");
      a.monoExpand = nodes[a.from].channels == 1 && nodes[a.to].channels == 2 && a.fromCh == 0 &&
                     a.toCh == 0 && a.channels == 1;
      if (a.monoExpand)
        a.channels = 2;
      edges.push_back(std::move(a));
    }
    std::vector<int> deg(nodes.size());
    for (auto &e : edges)
      ++deg[e.to];
    for (int i = 0; i < (int)nodes.size(); ++i)
      if (!deg[i])
        order.push_back(i);
    for (size_t i = 0; i < order.size(); ++i)
      for (auto &e : edges)
        if (e.from == order[i] && !--deg[e.to])
          order.push_back(e.to);
    if (order.size() != nodes.size())
      throw std::runtime_error("Feedback/cyclic audio routing rejected");
    recalculate();
    if (previous) retainHistories(*previous);
    for (auto &b : mediaBuffer)
      b.resize(bs);
  }
  // Immutable routing descriptions decide reuse. Never copy mutable delay
  // samples from the control thread while the callback is writing them.
  void retainHistories(const Graph &old) {
    std::vector<int> same(nodes.size(), -1);
    auto matchingEdge = [&](const Edge &e, const Edge &p) {
      return e.id == p.id && nodes[e.from].id == old.nodes[p.from].id && nodes[e.to].id == old.nodes[p.to].id &&
             e.fromCh == p.fromCh && e.toCh == p.toCh && e.channels == p.channels && e.desiredPre == p.desiredPre;
    };
    // Topological induction avoids recursive signature expansion in dense DAGs.
    for (int i : order) for (int k = 0; k < (int)old.nodes.size(); ++k) {
      auto &n = nodes[i]; const auto &p = old.nodes[k];
      if (n.id != p.id || n.kind != p.kind || n.channels != p.channels ||
          (n.kind == "input" && (*routes)[i] != (*old.routes)[k])) continue;
      bool inputsSame = true; int count = 0, oldCount = 0;
      for (const auto &e : edges) if (e.to == i) {
        ++count;
        auto found = std::find_if(old.edges.begin(), old.edges.end(), [&](const auto &v) { return matchingEdge(e, v) && same[e.from] == v.from; });
        if (found == old.edges.end()) inputsSame = false;
      }
      for (const auto &e : old.edges) if (e.to == k) ++oldCount;
      if (!inputsSame || count != oldCount) break;
      size_t s = 0;
      for (; s < n.inserts.size() && s < p.inserts.size(); ++s) {
        if (n.inserts[s].plugin != p.inserts[s].plugin) break;
        n.inserts[s].dry.retain(p.inserts[s].dry);
      }
      if (s == n.inserts.size() && s == p.inserts.size()) {
        same[i] = k; n.outputDelay.retain(p.outputDelay);
      }
      break;
    }
    for (auto &e : edges) for (const auto &p : old.edges)
      if (matchingEdge(e, p) && same[e.from] == p.from) e.delay.retain(p.delay);
  }
  static void validateControl(float gain, float pan, bool main = false) {
    constexpr float MaxMainGain = 3.981072f; // +12 dB, allowing float32 rounding.
    if (!std::isfinite(gain) || gain < 0 || gain > (main ? MaxMainGain : 16.f) || !std::isfinite(pan) || pan < -1 || pan > 1)
      throw std::runtime_error("Invalid control value");
  }
  void recalculate() {
    latency = 0;
    for (int i : order) {
      auto &n = nodes[i];
      int incoming = 0;
      for (auto &e : edges)
        if (e.to == i)
          incoming = std::max(incoming, nodes[e.from].latency);
      for (auto &e : edges)
        if (e.to == i)
          e.delay.setup(incoming - nodes[e.from].latency, e.channels);
      n.latency = incoming;
      n.pluginLatencySamples = 0;
      for (auto &p : n.inserts) {
        auto d = p.plugin->latency();
        p.dry.setup(d, n.channels);
        n.latency += d;
        n.pluginLatencySamples += d;
      }
      if (n.kind == "output")
        latency = std::max(latency, n.latency);
    }
    for (auto &n : nodes)
      if (n.kind == "output")
        n.outputDelay.setup(latency - n.latency, n.channels);
  }
  Plugin &find(const std::string &id) {
    for (auto &n : nodes)
      for (auto &p : n.inserts)
        if (p.plugin->id == id)
          return *p.plugin;
    throw std::runtime_error("Plugin instance missing");
  }
  void controls(const Json &j) {
    std::vector<Control> prepared;
    for (auto &c : j.value("nodes", Json::array())) {
      auto it =
          std::find_if(nodes.begin(), nodes.end(), [&](auto &n) { return n.id == c.at("id"); });
      if (it == nodes.end())
        throw std::runtime_error("Control node missing");
      float g = c.value("gain", it->desiredGain), p = c.value("pan", it->desiredPan);
      validateControl(g, p, true);
      Control n; n.index = (int)(it - nodes.begin()); n.gain = g; n.pan = p;
      n.mute = c.value("mute", it->desiredMute); n.monitor = c.value("monitor", it->desiredMonitor);
      prepared.push_back(n);
      for (auto &plugin : c.value("plugins", Json::array())) {
        auto pi = std::find_if(it->inserts.begin(), it->inserts.end(), [&](auto &x) {
          return x.plugin->id == plugin.at("instanceId");
        });
        if (pi == it->inserts.end()) throw std::runtime_error("Control plugin instance missing from node");
        Control q; q.kind = 2; q.index = n.index; q.insertIndex = (int)(pi - it->inserts.begin());
        q.wet = plugin.value("wet", pi->desiredWet); q.bypass = plugin.value("bypass", pi->desiredBypass);
        if (!std::isfinite(q.wet) || q.wet < 0 || q.wet > 1) throw std::runtime_error("Invalid plugin wet value");
        prepared.push_back(q);
      }
    }
    for (auto &c : j.value("edges", Json::array())) {
      auto it =
          std::find_if(edges.begin(), edges.end(), [&](auto &e) { return e.id == c.at("id"); });
      if (it == edges.end())
        throw std::runtime_error("Control edge id missing");
      float g = c.value("gain", it->desiredGain);
      validateControl(g, 0, it->main);
      auto tap = c.value("source", it->desiredPre ? "pre" : "post");
      if (tap != "pre" && tap != "post") throw std::runtime_error("Invalid route source tap");
      Control q; q.kind = 1; q.index = (int)(it - edges.begin()); q.gain = g; q.pre = tap == "pre";
      prepared.push_back(q);
    }
    auto w = controlWrite.load(std::memory_order_relaxed),
         r = controlRead.load(std::memory_order_acquire);
    if (w - r + prepared.size() > controlQueue.size())
      throw std::runtime_error("Control queue full; retry next block");
    for (auto &c : prepared) {
      if (!c.kind) {
        auto &n = nodes[c.index];
        n.desiredGain = c.gain;
        n.desiredPan = c.pan;
        n.desiredMute = c.mute;
        n.desiredMonitor = c.monitor;
      } else if (c.kind == 1) {
        edges[c.index].desiredGain = c.gain;
        edges[c.index].desiredPre = c.pre;
      } else {
        auto &p = nodes[c.index].inserts[c.insertIndex];
        p.desiredWet = c.wet; p.desiredBypass = c.bypass;
      }
      controlQueue[w++ % controlQueue.size()] = c;
    }
    controlWrite.store(w, std::memory_order_release);
  }
  void applyControls() {
    auto r = controlRead.load(std::memory_order_relaxed),
         w = controlWrite.load(std::memory_order_acquire);
    while (r < w) {
      auto &c = controlQueue[r++ % controlQueue.size()];
      if (c.kind == 1) {
        edges[c.index].gain = c.gain;
        edges[c.index].pre = c.pre;
      } else if (c.kind == 2) {
        auto &p = nodes[c.index].inserts[c.insertIndex];
        p.wet = c.wet; p.bypass = c.bypass;
      } else {
        auto &n = nodes[c.index];
        n.gain = c.gain;
        n.pan = c.pan;
        n.mute = c.mute;
        n.monitor = c.monitor;
      }
    }
    controlRead.store(r, std::memory_order_release);
  }
  void process(const float *input, float *output, int n, int inputChannels, int outputChannels,
               Media &media, uint64_t position) {
    applyControls();
    const auto &hardware = *activeRoutes.load();
    std::fill_n(output, (size_t)n * outputChannels, 0.f);
    for (auto &a : nodes)
      for (int c = 0; c < 2; ++c)
        std::fill_n(a.pre[c].data(), n, 0.f);
    bool hasMedia = false;
    for (auto &a : nodes)
      if (a.kind == "media")
        hasMedia = true;
    if (hasMedia) {
      float *mp[]{mediaBuffer[0].data(), mediaBuffer[1].data()};
      media.read(mp, n);
    }
    for (int ix : order) {
      auto &a = nodes[ix];
      if (a.kind == "input" && a.monitor && input)
        for (int c = 0; c < a.channels; ++c)
          for (int channel : hardware[ix][c]) if (channel < inputChannels)
            for (int s = 0; s < n; ++s)
              a.pre[c][s] += input[(size_t)s * inputChannels + channel];
      if (a.kind == "media")
        for (int c = 0; c < a.channels; ++c)
          for (int s = 0; s < n; ++s)
            a.pre[c][s] += mediaBuffer[c][s];
      for (auto &p : a.inserts) {
        float *in[]{a.pre[0].data(), a.pre[1].data()};
        float *out[]{a.scratch[0].data(), a.scratch[1].data()};
        if (!p.bypass)
          p.plugin->process(in, out, n, position);
        for (int c = 0; c < a.channels; ++c)
          for (int s = 0; s < n; ++s) {
            float dry = p.dry.step(in[c][s], c, s);
            a.pre[c][s] = p.bypass ? dry : dry * (1 - p.wet) + out[c][s] * p.wet;
          }
        p.dry.advance(n);
      }
      float gl = a.mute ? 0 : a.gain * (a.channels > 1 && a.pan > 0 ? 1 - a.pan : 1),
            gr = a.mute ? 0 : a.gain * (a.pan < 0 ? 1 + a.pan : 1);
      std::array<float, 2> peak{};
      std::array<double, 2> squares{};
      for (int c = 0; c < a.channels; ++c) {
        float start = c ? a.gainHistory->right : a.gainHistory->left, end = c ? gr : gl;
        for (int s = 0; s < n; ++s) {
          float v = a.mute ? 0.f : a.pre[c][s] * (start + (end - start) * (s + 1) / n);
          if (!std::isfinite(v))
            v = 0;
          a.post[c][s] = v;
          peak[c] = std::max(peak[c], std::abs(v));
          squares[c] += double(v) * v;
        }
      }
      a.gainHistory->left = gl;
      a.gainHistory->right = gr;
      a.meter->publish(peak[0], a.channels > 1 ? peak[1] : peak[0],
                       squares[0], a.channels > 1 ? squares[1] : squares[0], n);
      if (a.kind == "output")
        for (int c = 0; c < a.channels; ++c)
          for (int s = 0; s < n; ++s) {
            const float delayed = a.outputDelay.step(a.post[c][s], c, s);
            if (!a.mute)
              for (int channel : hardware[ix][c]) if (channel < outputChannels)
                output[(size_t)s * outputChannels + channel] += delayed;
          }
      a.outputDelay.advance(n);
      for (auto &e : edges)
        if (e.from == ix) {
          auto &dest = nodes[e.to];
          auto &src = e.pre ? a.pre : a.post;
          for (int c = 0; c < e.channels; ++c)
            for (int s = 0; s < n; ++s)
            {
              auto delayed = e.delay.step(a.mute ? 0.f : src[e.monoExpand ? 0 : e.fromCh + c][s], c, s);
              if (!a.mute) dest.pre[e.toCh + c][s] += delayed * e.gain *
                  (e.monoExpand && !e.pre
                       ? (c == 0 ? (a.pan > 0 ? 1 - a.pan : 1) : (a.pan < 0 ? 1 + a.pan : 1))
                       : 1);
            }
          e.delay.advance(n);
        }
    }
  }
};
class Engine {
#ifdef YIDECK_ENGINE_AUDIT
  friend struct RackGraphEditAudit;
#endif
  std::unique_ptr<RtAudio> audio;
#ifdef YIDECK_ENABLE_ASIO
  std::unique_ptr<yideck_asio::Backend> asio;
#endif
  std::unique_ptr<EndpointBridge> bridge;
  std::vector<EndpointSpec> endpointSpecs;
  std::unique_ptr<Graph> graph;
  std::unique_ptr<Graph> editCandidate;
  uint64_t graphEditSequence = 0, pendingGraphEdit = 0;
  bool editCandidateEndpointsReady = true;
  std::string editCandidateHardwareIssue;
  std::vector<std::unique_ptr<Graph>> retired;
  std::vector<std::unique_ptr<Graph::HardwareRoutes>> retiredRoutes;
  std::atomic<Graph *> activeGraph{nullptr};
  Json config = Json::object();
  Media media;
  OutputSignal outputSignal;
  std::atomic<bool> gate{true};
  std::atomic<int> inFlight{0}, deviceLatency{0};
  std::atomic<uint64_t> processed{0}, callbacks{0}, xruns{0}, gateSkips{0}, deadlineMisses{0},
      callbackNanos{0}, maxCallbackNanos{0};
  int inCh = 2, outCh = 2, block = 256;
  double rate = 48000;
  bool running = false;
  bool graphEndpointsReady = true;
  std::string graphHardwareIssue;
  std::string error;
  bool asioMode() const { return config.value("api", "wasapi") == "asio"; }
  bool nativeRunning() const {
#ifdef YIDECK_ENABLE_ASIO
    if (asioMode()) return asio && asio->running();
#endif
    return bridge ? bridge->allRunning() : audio && audio->isStreamRunning();
  }
  struct Guard {
    Engine &e;
    explicit Guard(Engine &x) : e(x) {
      e.gate.store(false);
      while (e.inFlight.load())
        Sleep(1);
    }
    ~Guard() { e.gate.store(true); }
  };
  static int callback(void *out, void *in, unsigned int frames, double, RtAudioStreamStatus status,
                      void *ctx) {
    auto &e = *(Engine *)ctx;
    e.processBlock((float *)out, (float *)in, frames, status,
                   (int)e.audio->getOutputQueueFrames());
    return 0;
  }
  static void endpointCallback(void *ctx, float *out, const float *in, unsigned frames,
                               unsigned status, int latency) {
    ((Engine *)ctx)->processBlock(out, in, frames, status, latency);
  }
  void processBlock(float *out, const float *in, unsigned frames, unsigned status, int dl) {
    auto &e = *this;
    ++e.inFlight;
    ++e.callbacks;
    if (status)
      ++e.xruns;
    auto *g = e.activeGraph.load();
    if (!e.gate.load() || !g || frames > (unsigned)e.block) {
      ++e.gateSkips;
      std::fill_n((float *)out, (size_t)frames * e.outCh, 0.f);
      --e.inFlight;
      return;
    }
    e.deviceLatency = dl;
    e.media.latency = dl + g->latency;
    auto t = std::chrono::steady_clock::now();
    g->process((float *)in, (float *)out, frames, e.inCh, e.outCh, e.media, e.processed.load());
    e.outputSignal.observe(out, frames, e.outCh);
    e.processed += frames;
    auto ns = (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
                  std::chrono::steady_clock::now() - t)
                  .count();
    if (ns > frames / e.rate * 1e9)
      ++e.deadlineMisses;
    e.callbackNanos += ns;
    auto old = e.maxCallbackNanos.load();
    while (ns > old && !e.maxCallbackNanos.compare_exchange_weak(old, ns)) {
    }
    --e.inFlight;
  }
  RtAudio &device() {
    if (!audio) {
      audio = std::make_unique<RtAudio>(RtAudio::WINDOWS_WASAPI);
      audio->showWarnings(false);
    }
    return *audio;
  }
  unsigned resolve(const Json &j, const char *key, bool input, unsigned expectedRate = 0) {
    if (!j.contains(key))
      throw std::runtime_error(std::string("Explicit ") + key + " is required");
    auto &s = j[key];
    for (auto id : device().getDeviceIds()) {
      auto d = device().getDeviceInfo(id);
      bool match = s.is_string() ? s.get<std::string>() == d.stableId
                   : s.is_number_unsigned() || s.is_number_integer() ? s.get<unsigned>() == id
                                                                     : false;
      if (match && (input ? d.inputChannels : d.outputChannels) > 0) {
        if (d.preferredSampleRate != (expectedRate ? expectedRate : (unsigned)rate))
          throw std::runtime_error("Configured rate must match endpoint Windows mix format; no "
                                   "implicit resampling");
        return id;
      }
    }
    throw std::runtime_error("Configured WASAPI endpoint unavailable");
  }
  void validatePair(const Json &j, unsigned expectedRate) {
    auto input = resolve(j, "inputDeviceId", true, expectedRate);
    auto output = resolve(j, "outputDeviceId", false, expectedRate);
    validateDeviceGroup(device().getDeviceInfo(input).groupId,
                        device().getDeviceInfo(output).groupId);
  }
  std::vector<EndpointSpec> prepareEndpoints(const Json &j, unsigned rate, const Json *asioInfo = nullptr) {
    const auto inputs = j.value("inputEndpoints", Json::array());
    const auto outputs = j.value("outputEndpoints", Json::array());
    if (!inputs.is_array() || !outputs.is_array() || outputs.empty() || inputs.size() + outputs.size() > 32)
      throw std::runtime_error("Select 1 to 32 endpoints including an output clock");
    const auto mode = j.value("ioMode", "single");
    const auto clock = j.value("clockOutputDeviceId", outputs.front().at("deviceId").get<std::string>());
    if (asioInfo && (asioInfo->at("stableId") != clock ||
        (mode == "single" && (inputs.size() > 1 || outputs.size() != 1))))
      throw std::runtime_error("Single ASIO uses one selected driver with one output clock");
    std::vector<EndpointSpec> specs;
    std::set<std::pair<bool, std::string>> selected;
    std::string group;
    bool clockFound = false;
    unsigned total = 0;
    for (bool input : {true, false}) {
      unsigned offset = 0;
      for (auto &p : input ? inputs : outputs) {
        auto id = p.at("deviceId").get<std::string>();
        int channels = p.at("channels").get<int>();
        bool primary = !input && id == clock;
        if (id.empty() || channels < 1 || channels > 256 || !selected.emplace(input, id).second)
          throw std::runtime_error("Invalid or duplicate endpoint/channel selection");
        EndpointSpec spec{id, "", (unsigned)channels, offset, input, primary};
        if (asioInfo && id == clock) {
          spec.asio = true;
          spec.groupId = clock;
          spec.nativeRate = rate;
          if (channels > asioInfo->at(input ? "inputChannels" : "outputChannels").get<int>())
            throw std::runtime_error("ASIO channel selection exceeds driver bounds");
        } else {
          if (id.starts_with("asio:") || (asioInfo && mode == "single"))
            throw std::runtime_error("Only the selected ASIO clock driver is supported");
          bool found = false;
          for (auto runtime : device().getDeviceIds()) {
            auto info = device().getDeviceInfo(runtime);
            if (info.stableId != id) continue;
            found = true;
            if ((unsigned)channels > (input ? info.inputChannels : info.outputChannels))
              throw std::runtime_error("Selected channels exceed the actual endpoint channel count");
            if (!info.preferredSampleRate || ((primary || mode == "single") && info.preferredSampleRate != rate))
              throw std::runtime_error("Clock or single-device rate must match endpoint Windows mix format");
            spec.groupId = info.groupId;
            spec.nativeRate = info.preferredSampleRate;
            if (mode == "single") {
              if (group.empty()) group = spec.groupId;
              validateDeviceGroup(group, spec.groupId);
            }
            break;
          }
          if (!found && (primary || mode == "single"))
            throw std::runtime_error("Configured WASAPI endpoint unavailable");
        }
        specs.push_back(std::move(spec));
        clockFound |= primary;
        offset += channels; total += channels;
      }
    }
    if (!clockFound) throw std::runtime_error("Clock output must be one of the selected output endpoints");
    if (total > 256) throw std::runtime_error("At most 256 configured endpoint channels in total");
    return specs;
  }
  Json endpointLayout() const {
    Json result = Json::array();
    for (auto &p : endpointSpecs)
      result.push_back({{"deviceId", p.deviceId}, {"input", p.input}, {"channels", p.channels},
                        {"channelOffset", p.offset}, {"clock", p.clock}, {"sampleRate", p.nativeRate}});
    return result;
  }
  Json mapGraphEndpoints(Json definition, bool &hardwareReady, std::string &hardwareIssue) const {
    hardwareReady = true;
    hardwareIssue.clear();
    for (auto &n : definition.at("nodes")) {
      n.erase("flatRoutes");
      auto kind = n.at("kind").get<std::string>();
      if (kind != "input" && kind != "output") continue;
      if (n.contains("hardwareRoutes")) {
        const auto width = n.at("channels").get<int>();
        const auto &targets = n.at("hardwareRoutes");
        if (width < 1 || width > 2 || !targets.is_array() || targets.size() > 512 ||
            (kind == "input" && targets.size() > 1))
          throw std::runtime_error("Invalid hardware route matrix");
        Graph::ChannelRoutes flat;
        for (const auto &target : targets) {
          const auto id = target.at("endpointId").get<std::string>();
          const auto channels = target.at("deviceChannels").get<std::vector<int>>();
          if (id.empty() || channels.size() != (size_t)width)
            throw std::runtime_error("Invalid endpoint or route width");
          const auto endpoint = std::find_if(endpointSpecs.begin(), endpointSpecs.end(), [&](const auto &spec) {
            return spec.deviceId == id && spec.input == (kind == "input");
          });
          for (int c = 0; c < width; ++c) {
            if (channels[c] < -1 || channels[c] >= 256) throw std::runtime_error("Invalid physical channel");
            if (channels[c] == -1 || endpoint == endpointSpecs.end()) continue;
            if (channels[c] >= (int)endpoint->channels) {
              if (running) throw std::runtime_error("Graph channel exceeds its selected endpoint range");
              hardwareReady = false; hardwareIssue = "channel-range"; continue;
            }
            int channel = endpoint->offset + channels[c];
            if (std::find(flat[c].begin(), flat[c].end(), channel) == flat[c].end())
              flat[c].push_back(channel);
          }
        }
        n["flatRoutes"] = flat;
        continue;
      }
      auto id = n.contains("endpointId") && n["endpointId"].is_string()
                    ? n["endpointId"].get<std::string>() : "";
      if (id.empty()) {
        if (!endpointSpecs.empty() || n.contains("endpointId")) {
          hardwareReady = false;
          if (hardwareIssue.empty()) hardwareIssue = "endpoint-unconfigured";
          if (running) throw std::runtime_error("Each hardware graph node requires an explicit endpointId");
        }
        continue;
      }
      auto it = std::find_if(endpointSpecs.begin(), endpointSpecs.end(), [&](auto &p) {
        return p.deviceId == id && p.input == (kind == "input");
      });
      if (it == endpointSpecs.end()) {
        hardwareReady = false;
        if (hardwareIssue.empty()) hardwareIssue = "endpoint-unconfigured";
        if (running) throw std::runtime_error("Graph hardware endpoint is not configured");
        continue; // Stopped native plugin editing does not require the original sound card.
      }
      auto channels = n.value("deviceChannels", std::vector<int>{0, 1});
      if (std::any_of(channels.begin(), channels.end(), [&](int c) { return c < 0 || c >= (int)it->channels; })) {
        if (running)
          throw std::runtime_error("Graph channel exceeds its selected endpoint range");
        // stop/clearGraph deliberately retains the last device selection. A new
        // stopped project may address another pair on that same endpoint. Its
        // plugins can still be edited; start must wait for explicit configure.
        hardwareReady = false;
        hardwareIssue = "channel-range";
        continue;
      }
      for (auto &c : channels) c += it->offset;
      n["deviceChannels"] = channels;
    }
    return definition;
  }
  void stop() {
#ifdef YIDECK_ENABLE_ASIO
    if (asio) asio->releaseHardware();
#endif
    if (bridge) bridge->stop();
    if (audio && audio->isStreamRunning())
      audio->stopStream();
    running = false;
  }

public:
  ~Engine() {
    stop();
    if (audio && audio->isStreamOpen())
      audio->closeStream();
    graph.reset();
  }
  bool shutdown = false;
  Json command(const Json &j) {
    auto op = j.at("op").get<std::string>();
    if (op == "ping")
      return {{"protocol", 1},
              {"version", "1.2.0"},
              {"capabilities",
               {{"wasapi", true},
#ifdef YIDECK_ENABLE_ASIO
                {"asio", true},
#else
                {"asio", false},
#endif
                {"vst3", true},
                {"vst2", true},
                {"graph", true},
                {"pdc", true},
                {"pluginEditor", true},
                {"mediaRing", true},
                {"intervalMeters", true},
                {"internalFloat32", true},
                {"outputSignalObservation", true},
                {"liveInsertAndSendControls", true},
                {"sameDeviceMultiEndpoint", true}, {"independentWasapiEndpoints", true},
                {"asioWasapiHybrid",
#ifdef YIDECK_ENABLE_ASIO
                 true
#else
                 false
#endif
                }}}};
    if (op == "devices") {
      Json ds = Json::array();
      if (j.value("api", "wasapi") == "asio") {
#ifdef YIDECK_ENABLE_ASIO
        for (auto &d : yideck_asio::registeredDrivers()) ds.push_back(yideck_asio::descriptor(d));
        return {{"devices", ds}};
#else
        throw std::runtime_error("ASIO is disabled in this build");
#endif
      }
      if (j.value("api", "wasapi") != "wasapi") throw std::runtime_error("Unsupported audio API");
      for (auto id : device().getDeviceIds()) {
        auto d = device().getDeviceInfo(id);
        ds.push_back(
            {{"id", d.stableId},
             {"stableId", d.stableId},
             {"groupId", d.groupId},
             {"groupName", d.groupName},
             {"manufacturer", d.manufacturer},
             {"runtimeId", id},
             {"name", d.name},
             {"inputChannels", d.inputChannels},
             {"outputChannels", d.outputChannels},
             {"sampleRates", (d.preferredSampleRate == 44100 || d.preferredSampleRate == 48000)
                                 ? Json::array({d.preferredSampleRate})
                                 : Json::array()},
             {"preferredSampleRate", d.preferredSampleRate},
             {"api", "wasapi"}});
      }
      return {{"devices", ds}};
    }
    if (op == "probeDevice" || op == "driverControlPanel") {
#ifdef YIDECK_ENABLE_ASIO
      if (running) throw std::runtime_error("Stop the rack before ASIO driver probe or control panel");
      if (j.value("api", "asio") != "asio") throw std::runtime_error("Driver probe/control panel requires ASIO");
      auto id = j.at("deviceId").get<std::string>();
      if (asio && asio->loaded() && asio->description().at("stableId") == id) {
        asio->close();
        if (op == "driverControlPanel") asio->panel();
        return {{"device", asio->query()}, {"controlPanelClosed", op == "driverControlPanel"}};
      }
      if (asio && asio->loaded()) throw std::runtime_error("Use an isolated process to probe another ASIO driver");
      yideck_asio::Backend selected(id);
      if (op == "driverControlPanel") selected.panel();
      return {{"device", selected.query()}, {"controlPanelClosed", op == "driverControlPanel"}};
#else
      throw std::runtime_error("ASIO is disabled in this build");
#endif
    }
    if (op == "configure") {
      if (running)
        throw std::runtime_error("Stop before configuring device");
      const bool useAsio = j.value("api", "wasapi") == "asio";
      if (j.value("api", "wasapi") != "wasapi" && !useAsio)
        throw std::runtime_error("Unsupported audio API");
#ifndef YIDECK_ENABLE_ASIO
      if (useAsio) throw std::runtime_error("ASIO is disabled in this build");
#endif
      const auto mode = j.value("ioMode", "single");
      if (mode != "single" && mode != (useAsio ? "hybrid" : "multi"))
        throw std::runtime_error("Incompatible audio API and ioMode");
      const bool multiple = j.contains("inputEndpoints") || j.contains("outputEndpoints");
      if (mode != "single" && (!multiple || !j.contains("clockOutputDeviceId")))
        throw std::runtime_error("Independent devices require an explicit output clock and endpoint selection");
      int ni = j.value("inputChannels", 0), no = j.value("outputChannels", 2),
          nb = j.value("bufferFrames", 256);
      double nr = j.value("sampleRate", 48000.0);
      if (((mode == "single" || !multiple) && (ni < 0 || ni > 64 || no < 1 || no > 64)) || nb < (useAsio ? 8 : 16) || nb > MaxBlock ||
          (nr != 48000 && nr != 44100))
        throw std::runtime_error("Invalid audio format; supported rates are 44100 and 48000 Hz");
      if (graph && (nb != block || nr != rate))
        throw std::runtime_error("Clear graph before changing block format");
      std::vector<EndpointSpec> specs;
#ifdef YIDECK_ENABLE_ASIO
      std::unique_ptr<yideck_asio::Backend> nextAsio;
      if (useAsio) {
        const auto outputs = j.value("outputEndpoints", Json::array());
        if (!outputs.is_array() || outputs.empty())
          throw std::runtime_error("ASIO requires an explicit clock output endpoint");
        const auto clock = j.value("clockOutputDeviceId", outputs.front().at("deviceId").get<std::string>());
        if (asio && asio->loaded()) asio->releaseHardware();
        nextAsio = std::make_unique<yideck_asio::Backend>(clock);
        auto &d = nextAsio->description();
        specs = prepareEndpoints(j, (unsigned)nr, &d);
        if (std::find(d["sampleRates"].begin(), d["sampleRates"].end(), (int)nr) == d["sampleRates"].end())
          throw std::runtime_error("ASIO selected sample rate is unsupported");
        if (!yideck_asio::validBuffer(nb,d["bufferMinFrames"],d["bufferMaxFrames"],d["bufferGranularity"]))
          throw std::runtime_error("ASIO selected buffer size does not match driver constraints");
      } else
#endif
      specs = multiple ? prepareEndpoints(j, (unsigned)nr) : std::vector<EndpointSpec>{};
      if (multiple) {
        ni = no = 0;
        for (auto &p : specs) (p.input ? ni : no) += p.channels;
      } else if (ni) validatePair(j, (unsigned)nr);
      if (graph && (specs.size() != endpointSpecs.size() ||
          !std::equal(specs.begin(), specs.end(), endpointSpecs.begin(), [](auto &a, auto &b) {
            return a.deviceId == b.deviceId && a.channels == b.channels &&
                   a.offset == b.offset && a.input == b.input && a.clock == b.clock;
          })))
        throw std::runtime_error("Clear graph before changing endpoint layout");
      if (bridge) { bridge->stop(); bridge.reset(); }
#ifdef YIDECK_ENABLE_ASIO
      if (audio && audio->isStreamOpen()) audio->closeStream();
      if(nextAsio) nextAsio->releaseHardware();
      asio = std::move(nextAsio);
#endif
      endpointSpecs = std::move(specs);
      inCh = ni;
      outCh = no;
      block = nb;
      rate = nr;
      config = j;
      outputSignal.reset();
      error.clear();
      return {{"sampleRate", rate}, {"bufferFrames", block}, {"configured", true}, {"ioMode", mode},
              {"inputChannels", inCh}, {"outputChannels", outCh},
              {"endpoints", endpointLayout()}};
    }
    if (op == "commitGraphEdit" || op == "rollbackGraphEdit") {
      if (!pendingGraphEdit || j.value("transaction", uint64_t(0)) != pendingGraphEdit)
        throw std::runtime_error("Graph edit transaction is stale or missing");
      if (op == "commitGraphEdit") {
        editCandidate->controls(j);
        editCandidate->applyControls(); // Not published yet: safe on control thread.
        activeGraph.store(editCandidate.get());
        if (graph) retired.push_back(std::move(graph));
        graph = std::move(editCandidate);
        graphEndpointsReady = editCandidateEndpointsReady;
        graphHardwareIssue = editCandidateHardwareIssue;
        media.latency = deviceLatency.load() + (graph ? graph->latency : 0);
        for (auto &n : graph->nodes) for (auto &p : n.inserts) p.plugin->acknowledgeRestart();
      } else {
        if (graph) graph->controls(j);
        editCandidate.reset();
      }
      pendingGraphEdit = 0;
      return {{"committed", op == "commitGraphEdit"}, {"hardwareReady", graph && graphEndpointsReady},
              {"hardwareIssue", graph ? graphHardwareIssue : ""}};
    }
    if (op == "setHardwareRoutes") {
      if (!graph || pendingGraphEdit) throw std::runtime_error("Hardware routes require an active graph");
      bool ready; std::string issue;
      const auto mapped = mapGraphEndpoints(j, ready, issue);
      auto routes = std::make_unique<Graph::HardwareRoutes>(*graph->routes);
      std::set<std::string> seen;
      for (const auto &n : mapped.at("nodes")) {
        auto id = n.at("id").get<std::string>();
        auto it = std::find_if(graph->nodes.begin(), graph->nodes.end(), [&](const Node &node) { return node.id == id; });
        if (it == graph->nodes.end() || (it->kind != "output" && it->kind != "input") ||
            n.at("kind") != it->kind || n.at("channels") != it->channels || !seen.insert(id).second)
          throw std::runtime_error("Hardware route does not match current graph");
        (*routes)[it - graph->nodes.begin()] = Graph::readRoutes(n, it->channels);
      }
      if (seen.size() != std::count_if(graph->nodes.begin(), graph->nodes.end(), [](const Node &n) {
            return n.kind == "input" || n.kind == "output";
          })) throw std::runtime_error("Hardware matrix must include every physical track");
      // The callback takes one immutable table at block entry. Old tables are
      // released by the control thread only after all earlier callbacks exit.
      retiredRoutes.push_back(std::move(graph->routes));
      graph->routes = std::move(routes);
      graph->activeRoutes.store(graph->routes.get());
      if (!inFlight.load()) retiredRoutes.clear();
      graphEndpointsReady = ready; graphHardwareIssue = issue;
      return {{"hardwareReady", ready}, {"hardwareIssue", issue}};
    }
    if (op == "setGraph" || op == "beginGraphEdit") {
      if (pendingGraphEdit) throw std::runtime_error("Finish pending graph edit first");
      bool hardwareReady;
      std::string hardwareIssue;
      auto definition = mapGraphEndpoints(j.at("graph"), hardwareReady, hardwareIssue);
      auto g = std::make_unique<Graph>(definition, block, rate, graph.get());
      if (op == "beginGraphEdit") {
        editCandidate = std::move(g);
        editCandidateEndpointsReady = hardwareReady;
        editCandidateHardwareIssue = hardwareIssue;
        pendingGraphEdit = ++graphEditSequence;
        return {{"graphLatencyFrames", editCandidate->latency}, {"nodeCount", editCandidate->nodes.size()},
                {"hardwareReady", hardwareReady}, {"hardwareIssue", hardwareIssue}, {"transaction", pendingGraphEdit}};
      }
      activeGraph.store(g.get());
      if (graph) retired.push_back(std::move(graph));
      graph = std::move(g);
      graphEndpointsReady = hardwareReady;
      graphHardwareIssue = hardwareIssue;
      for (auto &n : graph->nodes)
        for (auto &p : n.inserts)
          p.plugin->acknowledgeRestart();
      error.clear();
      media.latency = deviceLatency.load() + graph->latency;
      return {{"graphLatencyFrames", graph->latency}, {"nodeCount", graph->nodes.size()},
              {"hardwareReady", graphEndpointsReady}, {"hardwareIssue", graphHardwareIssue}, {"transaction", pendingGraphEdit}};
    }
    if (op == "clearGraph") {
      if (pendingGraphEdit) throw std::runtime_error("Finish pending graph edit first");
      if (running)
        throw std::runtime_error("Stop before clearing graph; snapshot plugin state first");
      activeGraph.store(nullptr);
      if (graph)
        retired.push_back(std::move(graph));
      return {{"cleared", true}};
    }
    if (op == "setControls") {
      if (!graph)
        throw std::runtime_error("Graph missing");
      graph->controls(j);
      return Json::object();
    }
    if (op == "start") {
      if (!graph || config.empty())
        throw std::runtime_error("Explicit configuration and graph required");
      if (!graphEndpointsReady)
        throw std::runtime_error("Graph hardware endpoints are unresolved; configure and reapply graph before start");
      if (running)
        return {{"running", true}};
#ifdef YIDECK_ENABLE_ASIO
      if (asioMode()) {
        const bool hybrid = config.value("ioMode", "single") == "hybrid";
        const auto clock = std::find_if(endpointSpecs.begin(), endpointSpecs.end(), [](const auto &s) { return s.clock; });
        if (!asio) throw std::runtime_error("Reconfigure the selected ASIO driver before starting");
        if (asio->restartRequired()) throw std::runtime_error("ASIO driver requested reset; stop and reconfigure before starting");
        if (!asio->loaded()) asio=std::make_unique<yideck_asio::Backend>(clock->deviceId);
        try {
          asio->close();
          if (hybrid) {
            bridge = std::make_unique<EndpointBridge>();
            bridge->open(endpointSpecs, (unsigned)rate, block, endpointCallback, this, true);
            asio->open(bridge->asioInputs(), bridge->asioOutputs(), block, rate,
                       EndpointBridge::asioClock, bridge.get());
            bridge->start();
          } else {
            asio->open(inCh,outCh,block,rate,endpointCallback,this);
          }
          deviceLatency=asio->latency();media.latency=deviceLatency.load()+graph->latency;
          asio->start();running=true;error.clear();
        } catch (...) {
          asio->releaseHardware();
          if (bridge) bridge->stop();
          throw;
        }
        return {{"running",true},{"api","asio"},{"bufferFrames",block},{"sampleRate",rate},
          {"deviceLatencyFrames",deviceLatency.load()},{"bridgeBufferFrames",bridge ? bridge->buffering() : 0},{"endpoints",endpointLayout()}};
      }
#endif
      if (!endpointSpecs.empty()) {
        // Re-enumerate immediately before opening: a stale dialog must not hide a
        // Windows mix-rate, channel-layout, container or device change.
        const bool adaptive = config.value("ioMode", "single") == "multi";
        auto current = adaptive ? endpointSpecs : prepareEndpoints(config, (unsigned)rate);
        bridge = std::make_unique<EndpointBridge>();
        bridge->open(current, (unsigned)rate, block, endpointCallback, this, adaptive);
        bridge->start();
        deviceLatency = bridge->latency();
        media.latency = deviceLatency.load() + graph->latency;
        error.clear(); running = true;
        return {{"running", true}, {"bufferFrames", block}, {"sampleRate", rate},
                {"deviceLatencyFrames", deviceLatency.load()},
                {"bridgeBufferFrames", bridge->buffering()}, {"endpoints", endpointLayout()}};
      }
      if (inCh)
        validatePair(config, (unsigned)rate);
      auto &d = device();
      if (d.isStreamOpen())
        d.closeStream();
      RtAudio::StreamParameters output, input;
      output.deviceId = resolve(config, "outputDeviceId", false);
      output.nChannels = outCh;
      output.firstChannel = 0;
      if (inCh) {
        input.deviceId = resolve(config, "inputDeviceId", true);
        input.nChannels = inCh;
        input.firstChannel = 0;
      }
      unsigned actual = block;
      RtAudio::StreamOptions options;
      options.streamName = "Yideck Audio Rack";
      auto er = d.openStream(&output, inCh ? &input : nullptr, RTAUDIO_FLOAT32, (unsigned)rate,
                             &actual, callback, this, &options);
      if (er != RTAUDIO_NO_ERROR)
        throw std::runtime_error("WASAPI open failed: " + d.getErrorText());
      if (actual > (unsigned)block) {
        d.closeStream();
        throw std::runtime_error("Device needs larger block: " + std::to_string(actual));
      }
      deviceLatency = (int)d.getOutputQueueFrames();
      media.latency = deviceLatency.load() + graph->latency;
      if (d.startStream() != RTAUDIO_NO_ERROR)
        throw std::runtime_error("WASAPI start failed: " + d.getErrorText());
      running = true;
      return {{"running", true},
              {"bufferFrames", actual},
              {"sampleRate", d.getStreamSampleRate()},
              {"deviceLatencyFrames", deviceLatency.load()}};
    }
    if (op == "stop") {
      stop();
      return {{"running", false}};
    }
    if (op == "attachMedia") {
      Guard guard(*this);
      media.attach(j.at("name"), (unsigned)rate);
      return {{"capacityFrames", media.capacity}};
    }
    if (op == "detachMedia") {
      Guard guard(*this);
      bool detached = media.view && media.generation == j.value("generation", uint64_t(0));
      if (detached)
        media.detach();
      return {{"detached", detached}};
    }
    if (op == "resetMedia") {
      Guard guard(*this);
      media.reset(j.value("generation", uint64_t(0)));
      return {{"generation", media.generation.load()}};
    }
    if (op == "pluginState" || op == "setPluginState" || op == "setParameter" || op == "editor" ||
        op == "pluginInfo") {
      if (!graph)
        throw std::runtime_error("Graph missing");
      auto &p = graph->find(j.at("instanceId"));
      if (op == "pluginState") {
        if (running) p.prepareRunningState();
        if (!running)
          p.flushParametersStopped();
        const auto beforeFrames = processed.load(), beforeMisses = deadlineMisses.load();
        const auto started = std::chrono::steady_clock::now();
        auto state = p.state();
        if (running) {
          const auto elapsed = std::chrono::steady_clock::now() - started;
          const auto blockTime = std::chrono::duration<double>(block / rate);
          // A third-party chunk implementation may serialize itself against its
          // process callback. Do not report this as a seamless copy merely
          // because the running flag survived. No silence/dry gate is inserted.
          if (elapsed > blockTime * 2 && processed.load() == beforeFrames)
            throw std::runtime_error("snapshotFailed: audio did not advance during current plugin state capture; original graph retained");
          const auto finish = std::chrono::steady_clock::now() + blockTime * 2;
          while (inFlight.load() && std::chrono::steady_clock::now() < finish) Sleep(1);
          if (deadlineMisses.load() != beforeMisses)
            throw std::runtime_error("snapshotFailed: an audio callback overrun was observed during current plugin state capture; original graph retained");
        }
        return state;
      }
      if (op == "setPluginState") {
        if (running)
          throw std::runtime_error("Stop before state restore");
        p.flushParametersStopped();
        p.restore(j.at("state"));
        graph->recalculate();
      }
      if (op == "setParameter") {
        p.parameter(j.at("parameterId"), j.at("value"));
        if (!running)
          p.flushParametersStopped();
      }
      if (op == "editor") {
        if (j.contains("ownerWindowHandle"))
          setPluginEditorOwner(p,(HWND)(uintptr_t)j.at("ownerWindowHandle").get<uint64_t>());
        p.editor(j.value("show", true));
        bool activated = j.value("show",true) && activatePluginEditor(p);
        return {{"visible", p.window && IsWindowVisible(p.window)},
                {"windowHandle", (uint64_t)(uintptr_t)p.window},
                {"ownerWindowHandle", (uint64_t)(uintptr_t)(p.window ? GetWindow(p.window,GW_OWNER) : nullptr)},
                {"foregroundActivated",activated}};
      }
      if (op == "pluginInfo")
        return p.describe();
      return Json::object();
    }
    if (op == "offline") {
      if (running || !graph)
        throw std::runtime_error("Offline processing requires stopped prepared graph");
      int n = j.value("frames", block);
      if (n < 1 || n > block)
        throw std::runtime_error("Offline block exceeds prepared size");
      auto input = j.value("input", std::vector<float>((size_t)n * inCh));
      if (input.size() != (size_t)n * inCh)
        throw std::runtime_error("Offline input size mismatch");
      std::vector<float> out((size_t)n * outCh);
      graph->process(input.data(), out.data(), n, inCh, outCh, media, processed.load());
      outputSignal.observe(out.data(), n, outCh);
      processed += n;
      Json r{{"frames", n}, {"channels", outCh}};
      if (j.value("returnSamples", true))
        r["samples"] = out;
      return r;
    }
    if (op == "meters") {
      Json meters = Json::array();
      if (graph) for (auto &n : graph->nodes) {
        auto m = n.meter->take();
        meters.push_back({{"id",n.id},{"peakLeft",m.peakLeft},{"peakRight",m.peakRight},
          {"rmsLeft",m.rmsLeft()},{"rmsRight",m.rmsRight()},
          {"sampleCount",m.sampleCount},{"clipped",m.peakLeft>1 || m.peakRight>1}});
      }
      return {{"meters",meters}};
    }
    if (op == "status") {
      Json meters = Json::array();
      Json tracks = Json::array();
      bool restart = false;
      if (graph)
        for (auto &n : graph->nodes) {
          float l = n.meter->left.load(std::memory_order_relaxed),
                r = n.meter->right.load(std::memory_order_relaxed);
          meters.push_back({{"id", n.id},
                            {"peakLeft", l},
                            {"peakRight", n.channels > 1 ? r : l},
                            {"clipped", l > 1 || r > 1}});
          bool trackRestart = false;
          for (auto &p : n.inserts)
            trackRestart |= p.plugin->needsRestart();
          restart |= trackRestart;
          tracks.push_back({{"id", n.id},
                            {"pluginLatencySamples",
                             trackRestart ? Json(nullptr) : Json(n.pluginLatencySamples)}});
        }
      double duration = processed.load() / rate;
      auto consumed = media.consumed.load();
      auto latency = media.latency.load();
      Json endpoints = Json::array();
      const bool backendTiming = bridge || asioMode();
      uint64_t totalCallbackNanos = backendTiming ? 0 : callbackNanos.load();
      uint64_t totalXruns = backendTiming ? 0 : xruns.load();
      uint64_t totalDeadlineMisses = backendTiming ? 0 : deadlineMisses.load();
      uint64_t maximumCallbackNanos = backendTiming ? 0 : maxCallbackNanos.load();
      if (bridge) for (auto &p : bridge->endpoints())
      {
        const unsigned deviceRate = p->spec.nativeRate ? p->spec.nativeRate : (unsigned)rate;
        const auto queuedDeviceFrames = uint64_t(p->deviceQueue.load() * rate / deviceRate);
        const auto portError = p->fault.load();
        totalCallbackNanos += p->callbackNanos.load(); totalXruns += p->xruns.load();
        totalDeadlineMisses += p->deadlineMisses.load();
        maximumCallbackNanos = std::max(maximumCallbackNanos, p->maxCallbackNanos.load());
        endpoints.push_back({{"deviceId", p->spec.deviceId}, {"input", p->spec.input},
          {"channels", p->spec.channels}, {"clock", p->spec.clock}, {"sampleRate", p->spec.nativeRate},
          {"muted", portError != 0}, {"error", EndpointBridge::faultLabel(portError)},
          {"driftPpm", p->ring.driftPpm.load()},
          {"callbackCount", p->callbacks.load()}, {"frames", p->frames.load()},
          {"bridgeQueuedFrames", p->ring.available()}, {"deviceQueueFrames", p->deviceQueue.load()},
          {"outputLatencyFrames", p->spec.input ? Json(nullptr) : Json(p->ring.available() + queuedDeviceFrames + (graph ? graph->latency : 0))},
          {"bridgeUnderflows", p->underflows.load()}, {"bridgeOverflows", p->overflows.load()},
          {"warmupCallbacks", p->warmupCallbacks.load()}, {"xruns", p->xruns.load()},
          {"maxCallbackMs", p->maxCallbackNanos.load() / 1e6},
          {"callbackDeadlineMisses", p->deadlineMisses.load()}});
      }
      bool driverRestart = false;
      Json asioStatus = nullptr;
#ifdef YIDECK_ENABLE_ASIO
      if (asioMode() && asio) {
        asioStatus=asio->status();driverRestart=asio->restartRequired();
        totalXruns += asioStatus["xruns"].get<uint64_t>();
        totalCallbackNanos += asioStatus["callbackTotalNanoseconds"].get<uint64_t>();
        maximumCallbackNanos = std::max(maximumCallbackNanos, (uint64_t)(asioStatus["maxCallbackMs"].get<double>()*1e6));
        totalDeadlineMisses += asioStatus["callbackDeadlineMisses"].get<uint64_t>();
        if (bridge) for (const auto &p : endpointSpecs) if (p.asio)
          endpoints.push_back({{"deviceId", p.deviceId}, {"input", p.input}, {"channels", p.channels},
            {"clock", p.clock}, {"sampleRate", rate}, {"muted", driverRestart},
            {"error", driverRestart ? "ASIO clock stopped" : ""}, {"driftPpm", 0},
            {"callbackCount", asioStatus["callbackCount"]}, {"xruns", asioStatus["xruns"]}});
      }
#endif
      auto streamError = bridge && bridge->failure() ? bridge->failureMessage() : error;
      Json clockOutput = config.contains("outputDeviceId") ? config["outputDeviceId"] : Json(nullptr);
      for (auto &p : endpointSpecs) if (p.clock) clockOutput = p.deviceId;
      return {
          {"running", running && nativeRunning()},
          {"api", config.value("api", "wasapi")}, {"ioMode", config.value("ioMode", "single")},
          {"asio", asioStatus},
          {"driverResetRequired", driverRestart},
          {"hardwareReady", graph && graphEndpointsReady},
          {"hardwareIssue", graph ? graphHardwareIssue : ""},
          {"busy", false},
          {"message", restart ? "Plugin requested I/O or latency restart; rebuild graph" : streamError},
          {"restartRequired", restart || driverRestart || (bridge && bridge->failure())},
          {"error", streamError},
          {"sampleRate", rate},
          {"bufferFrames", block},
          {"processedFrames", processed.load()},
          {"callbackCount", callbacks.load()},
          {"xruns", totalXruns},
          {"gateSkipCount", gateSkips.load()},
          {"callbackDeadlineMisses", totalDeadlineMisses},
          {"callbackCpuPercent", duration > 0 ? totalCallbackNanos / 1e9 / duration * 100 : 0},
          {"cpuPercent", duration > 0 ? totalCallbackNanos / 1e9 / duration * 100 : 0},
          {"maxCallbackMs", maximumCallbackNanos / 1e6},
          {"graphLatencyFrames", graph ? graph->latency : 0},
          {"deviceLatencyFrames", deviceLatency.load()},
          {"outputLatencyFrames", latency},
          {"mediaConsumedFrames", consumed},
          {"mediaPlayedFrames", consumed > (uint64_t)latency ? consumed - latency : 0},
          {"mediaUnderruns", media.underruns.load()},
          {"mediaGeneration", media.generation.load()},
          {"mediaClockOutputDeviceId", clockOutput},
          {"meters", meters},
          {"tracks", tracks},
          {"outputSignal", {{"sampleFormat", "float32"},
            {"location", "final-mix-before-device-conversion"}, {"resetOn", "configure"},
            {"lastBlockPeak", outputSignal.lastBlockPeak.load(std::memory_order_relaxed)},
            {"peakSinceReset", outputSignal.peakSinceReset.load(std::memory_order_relaxed)},
            {"overRangeSamples", outputSignal.overRangeSamples.load(std::memory_order_relaxed)},
            {"nonFiniteSamples", outputSignal.nonFiniteSamples.load(std::memory_order_relaxed)},
            {"framesObserved", outputSignal.framesObserved.load(std::memory_order_relaxed)},
            {"channelCount", outCh}}},
          {"endpoints", endpoints},
          {"bridgeBufferFrames", bridge ? bridge->buffering() : 0}};
    }
    if (op == "shutdown") {
      stop();
      shutdown = true;
      return {{"stopped", true}};
    }
    throw std::runtime_error("Unsupported operation: " + op);
  }
  void idle() {
#ifdef YIDECK_ENABLE_ASIO
    if (running && asioMode() && asio) {
      asio->health();
      if (asio->restartRequired()) {
        stop();
        error="ASIO driver requested reset, format/latency change or stopped callbacks; output stopped. Reconfigure the selected driver.";
      }
    }
#endif
    if (running && bridge) bridge->checkHealth();
    if (running && bridge && !bridge->allRunning()) {
      error = bridge->failure() ? bridge->failureMessage()
                               : "An essential WASAPI endpoint stopped; all rack outputs stopped.";
      stop();
    }
    if (!inFlight.load()) {
      retired.clear();
      retiredRoutes.clear();
    }
    if (graph)
      for (auto &n : graph->nodes)
        for (auto &p : n.inserts) {
          p.plugin->idle();
          if (running && p.plugin->needsRestart()) {
            stop();
            error = "Plugin requested I/O/latency restart or its processor "
                    "failed; output stopped. Rebuild graph before restarting.";
          }
        }
  }
};
int main() {
  ProtocolOutput protocol;
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
  SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
  CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  initializePlugins();
  HANDLE wake = CreateEvent(nullptr, TRUE, FALSE, nullptr);
  std::mutex mutex;
  std::deque<std::string> lines;
  std::atomic<bool> eof = false;
  std::thread reader([&] {
    std::string line;
    while (std::getline(std::cin, line)) {
      if (line.size() > 128 * 1024 * 1024)
        break;
      {
        std::lock_guard lock(mutex);
        lines.push_back(std::move(line));
      }
      SetEvent(wake);
    }
    eof = true;
    SetEvent(wake);
  });
  {
    Engine engine;
    while (!engine.shutdown) {
      MsgWaitForMultipleObjects(1, &wake, FALSE, 10, QS_ALLINPUT);
      MSG m;
      while (PeekMessage(&m, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&m);
        DispatchMessage(&m);
      }
      std::deque<std::string> pending;
      {
        std::lock_guard lock(mutex);
        pending.swap(lines);
        ResetEvent(wake);
      }
      for (auto &line : pending) {
        Json id = nullptr;
        std::string requestInstance;
        try {
          auto req = Json::parse(line);
          id = req.value("id", Json());
          if (req.contains("instanceId") && req["instanceId"].is_string()) requestInstance=req["instanceId"];
          auto result = engine.command(req);
          protocol.write(Json{{"id", id}, {"ok", true}, {"result", result}});
        } catch (const PluginFailure &e) {
          protocol.write(Json{{"id",id},{"ok",false},{"error",e.what()},{"instanceId",e.instanceId}});
        } catch (const std::exception &e) {
          Json failure{{"id", id}, {"ok", false}, {"error", e.what()}};
          if (!requestInstance.empty()) failure["instanceId"]=requestInstance;
          protocol.write(failure);
        }
      }
      engine.idle();
      if (eof && pending.empty())
        break;
    }
  }
  CancelSynchronousIo(reader.native_handle());
  if (reader.joinable())
    reader.join();
  CloseHandle(wake);
  CoUninitialize();
  return 0;
}
