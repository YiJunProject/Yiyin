#pragma once
#include "plugins.h"
#include "iasiodrv.h"
#include <array>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>
#include <limits>

// ASIO is a separately licensed SDK, not covered by RtAudio's MIT license.
// Use its x64 IASIO ABI directly so enumeration never initializes other drivers.
namespace yideck_asio {
static_assert(std::atomic<uint64_t>::is_always_lock_free &&
                  std::atomic<unsigned>::is_always_lock_free &&
                  std::atomic<void *>::is_always_lock_free,
              "ASIO callbacks require lock-free atomics");
struct DriverEntry {
  std::string id, name;
  CLSID clsid{};
};
inline std::wstring regText(HKEY key, const wchar_t *name) {
  DWORD bytes = 0, type = 0;
  if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS ||
      (type != REG_SZ && type != REG_EXPAND_SZ) || bytes > 32768)
    return {};
  std::wstring s(bytes / sizeof(wchar_t), L'\0');
  if (RegQueryValueExW(key, name, nullptr, &type, (BYTE *)s.data(), &bytes) != ERROR_SUCCESS)
    return {};
  while (!s.empty() && !s.back())
    s.pop_back();
  if (type == REG_EXPAND_SZ) {
    std::wstring expanded(32768, L'\0');
    auto n = ExpandEnvironmentStringsW(s.c_str(), expanded.data(), (DWORD)expanded.size());
    if (!n || n > expanded.size())
      return {};
    expanded.resize(n - 1);
    s = std::move(expanded);
  }
  return s;
}
inline bool x64Pe(const std::wstring &path) {
  std::ifstream f(path, std::ios::binary);
  IMAGE_DOS_HEADER dos{};
  f.read((char *)&dos, sizeof(dos));
  if (!f || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < 0 ||
      dos.e_lfanew > 16 * 1024 * 1024)
    return false;
  f.seekg(dos.e_lfanew);
  DWORD signature = 0;
  IMAGE_FILE_HEADER h{};
  f.read((char *)&signature, sizeof(signature));
  f.read((char *)&h, sizeof(h));
  return f && signature == IMAGE_NT_SIGNATURE && h.Machine == IMAGE_FILE_MACHINE_AMD64;
}
inline std::vector<DriverEntry> registeredDrivers() {
  std::vector<DriverEntry> result;
  HKEY root = nullptr;
  if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\ASIO", 0, KEY_READ | KEY_WOW64_64KEY, &root) !=
      ERROR_SUCCESS)
    return result;
  for (DWORD i = 0;; ++i) {
    wchar_t name[512]{};
    DWORD size = 512;
    auto er = RegEnumKeyExW(root, i, name, &size, nullptr, nullptr, nullptr, nullptr);
    if (er == ERROR_NO_MORE_ITEMS)
      break;
    if (er != ERROR_SUCCESS)
      continue;
    HKEY entry = nullptr;
    if (RegOpenKeyExW(root, name, 0, KEY_READ | KEY_WOW64_64KEY, &entry) != ERROR_SUCCESS)
      continue;
    auto text = regText(entry, L"CLSID");
    RegCloseKey(entry);
    CLSID clsid{};
    if (FAILED(CLSIDFromString(text.c_str(), &clsid)))
      continue;
    wchar_t canonical[40]{};
    StringFromGUID2(clsid, canonical, 40);
    std::wstring key = L"CLSID\\" + std::wstring(canonical) + L"\\InprocServer32";
    HKEY server = nullptr;
    if (RegOpenKeyExW(HKEY_CLASSES_ROOT, key.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &server) !=
        ERROR_SUCCESS)
      continue;
    auto path = regText(server, nullptr);
    RegCloseKey(server);
    if (!x64Pe(path))
      continue;
    auto id = "asio:" + utf8(canonical);
    if (std::none_of(result.begin(), result.end(), [&](auto &p) { return p.id == id; }))
      result.push_back({id, utf8(name), clsid});
  }
  RegCloseKey(root);
  return result;
}
inline Json descriptor(const DriverEntry &d) {
  return {{"id", d.id},
          {"stableId", d.id},
          {"groupId", d.id},
          {"name", d.name},
          {"groupName", d.name},
          {"manufacturer", ""},
          {"api", "asio"},
          {"probeRequired", true},
          {"inputChannels", 0},
          {"outputChannels", 0},
          {"sampleRates", Json::array()},
          {"preferredSampleRate", 0},
          {"currentSampleRate", 0},
          {"inputChannelNames", Json::array()},
          {"outputChannelNames", Json::array()},
          {"bufferMinFrames", -1},
          {"bufferMaxFrames", -1},
          {"bufferPreferredFrames", -1},
          {"bufferGranularity", -1},
          {"inputLatencyFrames", -1},
          {"outputLatencyFrames", -1}};
}
inline DriverEntry findDriver(const std::string &id) {
  for (auto &d : registeredDrivers())
    if (d.id == id)
      return d;
  throw std::runtime_error("Selected x64 ASIO driver is not registered or its DLL is unavailable");
}
inline bool validBuffer(int n, int minimum, int maximum, int granularity) {
  if (n < minimum || n > maximum || n < 8 || n > 8192)
    return false;
  if (granularity == -1)
    return (n & (n - 1)) == 0;
  if (granularity > 0)
    return (n - minimum) % granularity == 0;
  return granularity == 0 && minimum == maximum;
}
struct SampleCodec {
  int bytes = 0, bits = 0;
  bool big = false, floating = false;
  explicit SampleCodec(long type) {
    big = type < 16;
    switch (type) {
    case ASIOSTInt16MSB:
    case ASIOSTInt16LSB:
      bytes = 2;
      bits = 16;
      break;
    case ASIOSTInt24MSB:
    case ASIOSTInt24LSB:
      bytes = 3;
      bits = 24;
      break;
    case ASIOSTInt32MSB:
    case ASIOSTInt32LSB:
      bytes = 4;
      bits = 32;
      break;
    case ASIOSTFloat32MSB:
    case ASIOSTFloat32LSB:
      bytes = 4;
      floating = true;
      break;
    case ASIOSTFloat64MSB:
    case ASIOSTFloat64LSB:
      bytes = 8;
      floating = true;
      break;
    case ASIOSTInt32MSB16:
    case ASIOSTInt32LSB16:
      bytes = 4;
      bits = 16;
      break;
    case ASIOSTInt32MSB18:
    case ASIOSTInt32LSB18:
      bytes = 4;
      bits = 18;
      break;
    case ASIOSTInt32MSB20:
    case ASIOSTInt32LSB20:
      bytes = 4;
      bits = 20;
      break;
    case ASIOSTInt32MSB24:
    case ASIOSTInt32LSB24:
      bytes = 4;
      bits = 24;
      break;
    default:
      throw std::runtime_error("ASIO driver channel has unsupported PCM/DSD sample type " +
                               std::to_string(type));
    }
  }
  uint64_t raw(const unsigned char *p) const noexcept {
    uint64_t v = 0;
    for (int i = 0; i < bytes; ++i)
      v |= uint64_t(p[i]) << (8 * (big ? bytes - 1 - i : i));
    return v;
  }
  float read(const unsigned char *p) const noexcept {
    auto v = raw(p);
    if (floating) {
      float x = bytes == 4 ? std::bit_cast<float>((uint32_t)v) : (float)std::bit_cast<double>(v);
      return std::isfinite(x) ? x : 0.f;
    }
    // Reduced-bit ASIO integer formats are signed, right-aligned in 32 bits.
    v &= (uint64_t(1) << bits) - 1;
    int64_t s =
        (v & (uint64_t(1) << (bits - 1))) ? (int64_t)(v | (~uint64_t(0) << bits)) : (int64_t)v;
    return (float)(s / double(uint64_t(1) << (bits - 1)));
  }
  void write(unsigned char *p, float sample) const noexcept {
    if (!std::isfinite(sample))
      sample = 0;
    uint64_t v;
    if (floating)
      v = bytes == 4 ? std::bit_cast<uint32_t>(sample) : std::bit_cast<uint64_t>((double)sample);
    else {
      double scale = double(uint64_t(1) << (bits - 1));
      auto s = (int64_t)std::clamp(std::round(double(sample) * scale), -scale, scale - 1);
      v = (uint64_t)s;
    }
    for (int i = 0; i < bytes; ++i)
      p[i] = (unsigned char)(v >> (8 * (big ? bytes - 1 - i : i)));
  }
};

class Backend {
public:
  using Process = void (*)(void *, float *, const float *, unsigned, unsigned, int);
  enum Notification : unsigned {
    Reset = 1,
    RateChanged = 2,
    LatencyChanged = 4,
    BufferChanged = 8,
    ClockChanged = 16,
    InvalidCallback = 32
  };

private:
  IASIO *driver = nullptr;
  HWND host = nullptr;
  DriverEntry entry;
  Json info;
  std::vector<ASIOBufferInfo> buffers;
  std::vector<SampleCodec> codecs;
  std::vector<float> input, output;
  ASIOCallbacks callbacks{};
  Process process = nullptr;
  void *context = nullptr;
  int inputs = 0, outputs = 0, frames = 0;
  double rate = 0;
  bool prepared = false, started = false, readySupported = false;
  std::atomic<bool> enabled{false};
  std::atomic<unsigned> notifications{0}, inFlight{0};
  std::atomic<uint64_t> count{0}, overloads{0}, samplePosition{0}, systemNanos{0},
      discontinuities{0}, maxNanos{0}, totalNanos{0}, deadlineMisses{0};
  std::atomic<uint64_t> integerOutputOverRangeSamples{0};
  std::atomic<bool> positionValid{false};
  std::atomic<int> outputLatency{-1}, inputLatency{-1};
  std::atomic<DWORD> lastCallbackTick{0};
  DWORD startTick = 0;
  inline static std::atomic<Backend *> selected{nullptr};
  template <class T> static uint64_t value64(const T &v) { return (uint64_t(v.hi) << 32) | v.lo; }
  static void switched(long index, ASIOBool) {
    if (auto *b = selected.load(std::memory_order_acquire)) {
      ASIOTime t{};
      if (b->driver->getSamplePosition(&t.timeInfo.samplePosition, &t.timeInfo.systemTime) ==
          ASE_OK)
        t.timeInfo.flags = kSamplePositionValid | kSystemTimeValid;
      b->render(index, &t);
    }
  }
  static ASIOTime *timed(ASIOTime *time, long index, ASIOBool) {
    if (auto *b = selected.load(std::memory_order_acquire))
      b->render(index, time);
    return time;
  }
  static void rateChanged(ASIOSampleRate sr) {
    if (auto *b = selected.load(std::memory_order_acquire))
      if (b->enabled.load(std::memory_order_relaxed) && sr != b->rate)
        b->notifications.fetch_or(RateChanged);
  }
  static long message(long selector, long value, void *, double *) {
    if (selector == kAsioSelectorSupported) {
      switch (value) {
      case kAsioResetRequest:
      case kAsioEngineVersion:
      case kAsioResyncRequest:
      case kAsioLatenciesChanged:
      case kAsioSupportsTimeInfo:
      case kAsioOverload:
        return 1;
      default:
        return 0;
      }
    }
    if (selector == kAsioEngineVersion)
      return 2;
    if (selector == kAsioSupportsTimeInfo)
      return 1;
    auto *b = selected.load(std::memory_order_acquire);
    if (!b)
      return 0;
    switch (selector) {
    case kAsioResetRequest:
      b->notifications.fetch_or(Reset);
      return 1;
    case kAsioLatenciesChanged:
      if (b->enabled.load())
        b->notifications.fetch_or(LatencyChanged);
      return 1;
    case kAsioBufferSizeChange:
      b->notifications.fetch_or(BufferChanged);
      return 0;
    case kAsioResyncRequest:
    case kAsioOverload:
      ++b->overloads;
      return 1;
    default:
      return 0;
    }
  }
  void render(long index, ASIOTime *time) noexcept {
    ++inFlight;
    if (index < 0 || index > 1) {
      notifications.fetch_or(InvalidCallback);
      --inFlight;
      return;
    }
    auto begin = std::chrono::steady_clock::now();
    ++count;
    lastCallbackTick = GetTickCount();
    if (time && (time->timeInfo.flags & kSamplePositionValid)) {
      auto position = value64(time->timeInfo.samplePosition);
      if (positionValid.load() && position != samplePosition.load() + frames)
        ++discontinuities;
      samplePosition = position;
      positionValid = true;
      if (time->timeInfo.flags & kSystemTimeValid)
        systemNanos = value64(time->timeInfo.systemTime);
      if (enabled.load()) {
        if ((time->timeInfo.flags & kSampleRateChanged) && time->timeInfo.sampleRate != rate)
          notifications.fetch_or(RateChanged);
        if (time->timeInfo.flags & kClockSourceChanged)
          notifications.fetch_or(ClockChanged);
      }
    }
    const bool active = enabled.load(std::memory_order_acquire) && !notifications.load();
    if (active) {
      for (int c = 0; c < inputs; ++c) {
        auto *p = (const unsigned char *)buffers[c].buffers[index];
        auto &codec = codecs[c];
        for (int s = 0; s < frames; ++s)
          input[(size_t)s * inputs + c] = codec.read(p + (size_t)s * codec.bytes);
      }
      std::fill(output.begin(), output.end(), 0.f);
      process(context, output.data(), inputs ? input.data() : nullptr, frames, 0,
              outputLatency.load());
    }
    uint64_t integerOverRange = 0;
    for (int c = 0; c < outputs; ++c) {
      auto ix = inputs + c;
      auto *p = (unsigned char *)buffers[ix].buffers[index];
      auto &codec = codecs[ix];
      if (!active)
        std::memset(p, 0, (size_t)frames * codec.bytes);
      else
        for (int s = 0; s < frames; ++s) {
          auto sample = output[(size_t)s * outputs + c];
          if (!codec.floating && std::isfinite(sample) && std::abs(sample) > 1.f)
            ++integerOverRange;
          codec.write(p + (size_t)s * codec.bytes, sample);
        }
    }
    integerOutputOverRangeSamples.fetch_add(integerOverRange, std::memory_order_relaxed);
    if (readySupported)
      driver->outputReady();
    auto ns = (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
                  std::chrono::steady_clock::now() - begin)
                  .count();
    totalNanos += ns;
    auto old = maxNanos.load();
    while (ns > old && !maxNanos.compare_exchange_weak(old, ns)) {
    }
    if (ns > uint64_t(frames / rate * 1e9))
      ++deadlineMisses;
    --inFlight;
  }
  static void check(ASIOError er, const char *operation) {
    if (er != ASE_OK)
      throw std::runtime_error(std::string("ASIO ") + operation + " failed (" + std::to_string(er) +
                               "); driver may be busy or unavailable");
  }

public:
#ifdef YIDECK_ASIO_TEST
  // Test-only injection exercises exactly the production callback/format path.
  Backend(IASIO *fixture, const DriverEntry &identity) : driver(fixture), entry(identity) {
    info = query();
  }
#endif
  explicit Backend(const std::string &id) : entry(findDriver(id)) {
    host = CreateWindowExW(0, L"STATIC", L"Yideck ASIO Host", WS_POPUP, 0, 0, 0, 0, nullptr,
                           nullptr, GetModuleHandleW(nullptr), nullptr);
    auto hr =
        CoCreateInstance(entry.clsid, nullptr, CLSCTX_INPROC_SERVER, entry.clsid, (void **)&driver);
    if (FAILED(hr) || !driver) {
      if (host)
        DestroyWindow(host);
      host = nullptr;
      throw std::runtime_error("Selected x64 ASIO driver could not be loaded (" +
                               std::to_string(hr) + ")");
    }
    if (!driver->init(host)) {
      char message[124]{};
      driver->getErrorMessage(message);
      driver->Release();
      driver = nullptr;
      DestroyWindow(host);
      host = nullptr;
      throw std::runtime_error(std::string("ASIO initialization failed: ") + message);
    }
    try {
      info = query();
    } catch (...) {
      driver->Release();
      driver = nullptr;
      DestroyWindow(host);
      host = nullptr;
      throw;
    }
  }
  ~Backend() { releaseHardware(); }
  Backend(const Backend &) = delete;
  Backend &operator=(const Backend &) = delete;
  Json query() {
    Json d = descriptor(entry);
    long ni = 0, no = 0, minimum = 0, maximum = 0, preferred = 0, granularity = 0;
    double sr = 0;
    check(driver->getChannels(&ni, &no), "getChannels");
    if (ni < 0 || no < 1 || ni + no > 256)
      throw std::runtime_error("ASIO channel count outside 256-channel host limit");
    check(driver->getSampleRate(&sr), "getSampleRate");
    check(driver->getBufferSize(&minimum, &maximum, &preferred, &granularity), "getBufferSize");
    d["probeRequired"] = false;
    d["inputChannels"] = ni;
    d["outputChannels"] = no;
    d["currentSampleRate"] = (int)sr;
    d["preferredSampleRate"] = (int)sr;
    for (auto r : {44100, 48000})
      if (driver->canSampleRate(r) == ASE_OK)
        d["sampleRates"].push_back(r);
    d["bufferMinFrames"] = minimum;
    d["bufferMaxFrames"] = maximum;
    d["bufferPreferredFrames"] = preferred;
    d["bufferGranularity"] = granularity;
    d["bufferPreferenceIsCurrentSize"] =
        false; // SDK has no current-buffer query before createBuffers.
    long il = -1, ol = -1;
    if (driver->getLatencies(&il, &ol) == ASE_OK) {
      d["inputLatencyFrames"] = il;
      d["outputLatencyFrames"] = ol;
    }
    for (int direction = 0; direction < 2; ++direction)
      for (long c = 0; c < (direction ? no : ni); ++c) {
        ASIOChannelInfo ci{};
        ci.channel = c;
        ci.isInput = direction ? ASIOFalse : ASIOTrue;
        check(driver->getChannelInfo(&ci), "getChannelInfo");
        // ASIO channel names are driver ANSI strings, unlike the UTF-16 registry.
        int bytes = (int)strnlen_s(ci.name, sizeof(ci.name));
        int length = MultiByteToWideChar(CP_ACP, 0, ci.name, bytes, nullptr, 0);
        std::wstring ws(length, L'\0');
        if (length)
          MultiByteToWideChar(CP_ACP, 0, ci.name, bytes, ws.data(), length);
        d[direction ? "outputChannelNames" : "inputChannelNames"].push_back(utf8(ws));
      }
    d["driverVersion"] = driver->getDriverVersion();
    return d;
  }
  const Json &description() const { return info; }
  void panel() {
    if (started)
      throw std::runtime_error("Stop ASIO before opening its control panel");
    check(driver->controlPanel(), "controlPanel");
    info = query();
  }
  void open(int ni, int no, int block, double sr, Process fn, void *ctx) {
    if (prepared)
      throw std::runtime_error("ASIO buffers already prepared");
    info = query();
    if (ni < 0 || no < 1 || ni > info["inputChannels"].get<int>() ||
        no > info["outputChannels"].get<int>())
      throw std::runtime_error("ASIO physical channel selection exceeds driver bounds");
    if ((sr != 44100 && sr != 48000) || driver->canSampleRate(sr) != ASE_OK)
      throw std::runtime_error("ASIO selected sample rate is unsupported");
    if (!validBuffer(block, info["bufferMinFrames"], info["bufferMaxFrames"],
                     info["bufferGranularity"]))
      throw std::runtime_error("ASIO selected buffer size does not match driver constraints");
    if (info["currentSampleRate"].get<int>() != (int)sr)
      check(driver->setSampleRate(sr), "setSampleRate");
    double actual = 0;
    check(driver->getSampleRate(&actual), "getSampleRate");
    if (actual != sr)
      throw std::runtime_error("ASIO driver did not apply the explicitly selected sample rate");
    inputs = ni;
    outputs = no;
    frames = block;
    rate = sr;
    process = fn;
    context = ctx;
    input.assign((size_t)ni * block, 0);
    output.assign((size_t)no * block, 0);
    buffers.clear();
    codecs.clear();
    buffers.reserve(ni + no);
    codecs.reserve(ni + no);
    for (int direction = 0; direction < 2; ++direction)
      for (int c = 0; c < (direction ? no : ni); ++c) {
        ASIOChannelInfo ci{};
        ci.channel = c;
        ci.isInput = direction ? ASIOFalse : ASIOTrue;
        check(driver->getChannelInfo(&ci), "getChannelInfo");
        codecs.emplace_back(ci.type);
        ASIOBufferInfo bi{};
        bi.channelNum = c;
        bi.isInput = ci.isInput;
        buffers.push_back(bi);
      }
    Backend *expected = nullptr;
    if (!selected.compare_exchange_strong(expected, this))
      throw std::runtime_error("Only one ASIO driver may be active in an engine process");
    notifications = 0;
    callbacks = {switched, rateChanged, message, timed};
    auto er = driver->createBuffers(buffers.data(), (long)buffers.size(), block, &callbacks);
    if (er != ASE_OK) {
      selected = nullptr;
      check(er, "createBuffers");
    }
    prepared = true;
    try {
      for (size_t i = ni; i < buffers.size(); ++i)
        for (int side = 0; side < 2; ++side) {
          if (!buffers[i].buffers[side])
            throw std::runtime_error("ASIO driver returned a null output buffer");
          std::memset(buffers[i].buffers[side], 0, (size_t)block * codecs[i].bytes);
        }
      for (int i = 0; i < ni; ++i)
        for (int side = 0; side < 2; ++side)
          if (!buffers[i].buffers[side])
            throw std::runtime_error("ASIO driver returned a null input buffer");
      long il = 0, ol = 0;
      check(driver->getLatencies(&il, &ol), "getLatencies");
      if (il < 0 || ol < 0)
        throw std::runtime_error("ASIO driver returned invalid latency");
      inputLatency = il;
      outputLatency = ol;
      readySupported = driver->outputReady() == ASE_OK;
      if (notifications.load())
        throw std::runtime_error("ASIO driver requested reset during buffer preparation");
    } catch (...) {
      close();
      throw;
    }
  }
  void start() {
    if (!prepared)
      throw std::runtime_error("ASIO buffers not prepared");
    if (notifications.load())
      throw std::runtime_error("ASIO driver requires reconfiguration");
    enabled = true;
    startTick = lastCallbackTick = GetTickCount();
    auto er = driver->start();
    if (er != ASE_OK) {
      enabled = false;
      // A failed start may still have acquired driver resources. Teardown is
      // on this control thread, never from an ASIO callback.
      driver->stop();
      releaseHardware();
      check(er, "start");
    }
    started = true;
  }
  void stop() {
    enabled = false;
    if (started) {
      driver->stop();
      started = false;
    }
    while (inFlight.load())
      Sleep(1);
  }
  void close() {
    stop();
    if (prepared) {
      driver->disposeBuffers();
      prepared = false;
    }
    if (selected.load() == this)
      selected = nullptr;
  }
  void releaseHardware() {
    close();
    if (driver) {
      driver->Release();
      driver = nullptr;
    }
    if (host) {
      DestroyWindow(host);
      host = nullptr;
    }
  }
  bool loaded() const { return driver != nullptr; }
  bool running() const { return started && !notifications.load(); }
  bool restartRequired() const { return notifications.load() != 0; }
  int latency() const { return outputLatency.load(); }
  void health() {
    if (started && GetTickCount() - startTick > 2000 &&
        GetTickCount() - lastCallbackTick.load() > 1000)
      notifications.fetch_or(Reset);
  }
  Json status() const {
    Json formats = Json::array();
    if (codecs.size() >= size_t(inputs + outputs))
      for (int c = 0; c < outputs; ++c) {
        auto &codec = codecs[inputs + c];
        formats.push_back({{"channel", c}, {"floatingPoint", codec.floating},
          {"containerBits", codec.bytes * 8}, {"validBits", codec.floating ? codec.bytes * 8 : codec.bits},
          {"bigEndian", codec.big}});
      }
    return {{"notificationFlags", notifications.load()},
            {"integerOutputOverRangeSamples", integerOutputOverRangeSamples.load(std::memory_order_relaxed)},
            {"outputSampleFormats", formats},
            {"driverResetRequired", restartRequired()},
            {"callbackCount", count.load()},
            {"xruns", overloads.load()},
            {"inputLatencyFrames", inputLatency.load()},
            {"outputLatencyFrames", outputLatency.load()},
            {"samplePositionValid", positionValid.load()},
            {"samplePosition", samplePosition.load()},
            {"systemTimeNanoseconds", systemNanos.load()},
            {"samplePositionDiscontinuities", discontinuities.load()},
            {"maxCallbackMs", maxNanos.load() / 1e6},
            {"callbackTotalNanoseconds", totalNanos.load()},
            {"callbackDeadlineMisses", deadlineMisses.load()},
            {"bufferFrames", frames},
            {"sampleRate", rate},
            {"inputChannels", inputs},
            {"outputChannels", outputs}};
  }
};
} // namespace yideck_asio
