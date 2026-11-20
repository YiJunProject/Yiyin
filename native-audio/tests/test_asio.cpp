#define YIDECK_ENGINE_AUDIT
#define main yideck_engine_entry_unused
#include "../src/engine.cpp"
#undef main
#include <iostream>
#include <cstdlib>
static std::atomic<bool> watch{false};
static std::atomic<int> allocations{0};
void *operator new(size_t n) {
  if (watch.load())
    ++allocations;
  if (auto p = std::malloc(n))
    return p;
  throw std::bad_alloc();
}
void operator delete(void *p) noexcept { std::free(p); }
void operator delete(void *p, size_t) noexcept { std::free(p); }
using namespace yideck_asio;
struct Fake final : IASIO {
  ASIOCallbacks cb{};
  std::vector<ASIOBufferInfo> buffers;
  std::vector<std::array<std::vector<unsigned char>, 2>> bytes;
  double rate = 48000;
  int size = 0, starts = 0, stops = 0, rateWrites = 0;
  bool failStart = false;
  long sampleType = ASIOSTInt32LSB;
  uint64_t position = 0;
  bool released = false;
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void **) override { return E_NOINTERFACE; }
  ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
  ULONG STDMETHODCALLTYPE Release() override {
    released = true;
    return 0;
  }
  ASIOBool init(void *) override { return ASIOTrue; }
  void getDriverName(char *n) override { strcpy_s(n, 32, "Yideck fixture"); }
  long getDriverVersion() override { return 1; }
  void getErrorMessage(char *s) override { s[0] = 0; }
  ASIOError start() override {
    ++starts;
    return failStart ? ASE_HWMalfunction : ASE_OK;
  }
  ASIOError stop() override {
    ++stops;
    return ASE_OK;
  }
  ASIOError getChannels(long *i, long *o) override {
    *i = 4;
    *o = 4;
    return ASE_OK;
  }
  ASIOError getLatencies(long *i, long *o) override {
    *i = 73;
    *o = 91;
    return ASE_OK;
  }
  ASIOError getBufferSize(long *a, long *b, long *c, long *d) override {
    *a = 16;
    *b = 1024;
    *c = 256;
    *d = -1;
    return ASE_OK;
  }
  ASIOError canSampleRate(double sr) override {
    return sr == 44100 || sr == 48000 ? ASE_OK : ASE_NoClock;
  }
  ASIOError getSampleRate(double *s) override {
    *s = rate;
    return ASE_OK;
  }
  ASIOError setSampleRate(double s) override {
    ++rateWrites;
    rate = s;
    return ASE_OK;
  }
  ASIOError getClockSources(ASIOClockSource *, long *) override { return ASE_NotPresent; }
  ASIOError setClockSource(long) override { return ASE_NotPresent; }
  ASIOError getSamplePosition(ASIOSamples *p, ASIOTimeStamp *t) override {
    p->hi = (unsigned long)(position >> 32);
    p->lo = (unsigned long)position;
    t->hi = 0;
    t->lo = 1;
    return ASE_OK;
  }
  ASIOError getChannelInfo(ASIOChannelInfo *c) override {
    c->type = sampleType;
    c->channelGroup = 0;
    strcpy_s(c->name, "Fixture channel");
    return ASE_OK;
  }
  ASIOError createBuffers(ASIOBufferInfo *b, long n, long s, ASIOCallbacks *c) override {
    cb = *c;
    size = s;
    bytes.resize(n);
    for (int i = 0; i < n; ++i)
      for (int side = 0; side < 2; ++side) {
        bytes[i][side].assign(s * SampleCodec(sampleType).bytes, 0x7f);
        b[i].buffers[side] = bytes[i][side].data();
      }
    buffers.assign(b, b + n);
    return ASE_OK;
  }
  ASIOError disposeBuffers() override { return ASE_OK; }
  ASIOError controlPanel() override { return ASE_OK; }
  ASIOError future(long, void *) override { return ASE_NotPresent; }
  ASIOError outputReady() override { return ASE_OK; }
  void tick() {
    ASIOTime t{};
    t.timeInfo.flags = kSamplePositionValid | kSystemTimeValid;
    t.timeInfo.samplePosition.hi = (unsigned long)(position >> 32);
    t.timeInfo.samplePosition.lo = (unsigned long)position;
    cb.bufferSwitchTimeInfo(&t, 0, ASIOTrue);
    position += size;
  }
};
struct RackGraphEditAudit {
  static void hybrid(auto &check) {
    Fake driver;
    DriverEntry id{"asio:test-hybrid", "Fixture hybrid", {}};
    Engine engine;
    engine.asio = std::make_unique<Backend>(&driver, id);
    Json main = {{"deviceId", id.id}, {"channels", 2}};
    Json absent = {{"deviceId", "WASAPI-missing-fixture"}, {"channels", 1}};
    Json cfg = {{"api", "asio"}, {"ioMode", "hybrid"}, {"clockOutputDeviceId", id.id},
                {"inputEndpoints", Json::array({absent, main})}, {"outputEndpoints", Json::array({absent, main})}};
    // Inject the driver's IASIO identity, reusing the production layout preparation.
    engine.config = cfg;
    engine.endpointSpecs = engine.prepareEndpoints(cfg, 48000, &engine.asio->description());
    engine.inCh = engine.outCh = 3;
    check(engine.endpointSpecs[1].input && engine.endpointSpecs[1].offset == 1 &&
          engine.endpointSpecs[3].clock && engine.endpointSpecs[3].offset == 1,
          "hybrid accepts ASIO duplex after missing auxiliary on each side");
    auto corrupt = cfg;
    corrupt["outputEndpoints"][0]["deviceId"] = "asio:other";
    bool rejected = false;
    try { engine.prepareEndpoints(corrupt, 48000, &engine.asio->description()); }
    catch (const std::runtime_error &) { rejected = true; }
    check(rejected, "second ASIO driver rejected in hybrid");
    Json graph = {{"nodes", Json::array({
      {{"id","input"},{"kind","input"},{"endpointId",id.id},{"deviceChannels",{0,1}}},
      {{"id","output"},{"kind","output"},{"endpointId",id.id},{"deviceChannels",{0,1}}}})},
      {"edges",Json::array({{{"id","thru"},{"from","input"},{"to","output"}}})}};
    auto prepared = engine.command({{"op","setGraph"},{"graph",graph}});
    auto started = engine.command({{"op","start"}});
    check(prepared["hardwareReady"] && started["running"] && driver.buffers.size()==4,
          "engine starts one ASIO duplex driver at its physical width with two missing auxiliary streams");
    SampleCodec codec(driver.sampleType);
    for (int i=0; i<driver.size; ++i) {
      codec.write(driver.bytes[0][0].data()+i*4,.375f);
      codec.write(driver.bytes[1][0].data()+i*4,-.25f);
    }
    auto before=allocations.load();
    watch = true;
    for (int i=0; i<32; ++i) driver.tick();
    watch = false;
    auto status=engine.command({{"op","status"}});
    auto muted=std::count_if(status["endpoints"].begin(),status["endpoints"].end(),
                             [](const auto &p){ return p.value("muted",false); });
    check(allocations.load()==before && status["callbackCount"]==32 && status["processedFrames"]==32*256 &&
          codec.read(driver.bytes[2][0].data())==.375f && codec.read(driver.bytes[3][0].data())==-.25f,
          "production hybrid engine routes distinct stereo samples and processes once per fixture ASIO callback without allocation");
    check(muted==2 && status["endpoints"].size()==4 && status["running"] && status["mediaClockOutputDeviceId"]==id.id,
          "hybrid status retains missing auxiliary input/output and advancing ASIO master");
    driver.cb.asioMessage(kAsioResetRequest,0,nullptr,nullptr);
    engine.idle();
    status=engine.command({{"op","status"}});
    check(driver.stops==1 && driver.released && !status["running"].get<bool>() && status["restartRequired"],
          "ASIO clock reset stops the aggregate and releases the driver");
  }
};
int main() {
  int checks = 0;
  Json results = Json::array();
  auto check = [&](bool b, const char *n) {
    ++checks;
    if (!b)
      throw std::runtime_error(n);
    results.push_back(n);
  };
  try {
    for (long type : {0, 1, 2, 3, 4, 8, 9, 10, 11, 16, 17, 18, 19, 20, 24, 25, 26, 27}) {
      SampleCodec c(type);
      unsigned char data[8]{};
      for (float x : {-1.f, -.5f, 0.f, .25f, .999f}) {
        c.write(data, x);
        check(std::abs(c.read(data) - x) < .00004f, "PCM conversion roundtrip");
      }
      c.write(data, std::numeric_limits<float>::quiet_NaN());
      check(c.read(data) == 0, "nonfinite input zeroed");
      for (float x : {-16.f, -1.75f, 1.5f, 16.f}) {
        c.write(data, x);
        if (c.floating)
          check(c.read(data) == x, "ASIO float preserves finite overscale rather than clipping");
        else {
          auto expected = x < 0 ? (uint64_t(1) << (c.bits-1)) : (uint64_t(1) << (c.bits-1)) - 1;
          check((c.raw(data) & ((uint64_t(1) << c.bits)-1)) == expected,
                "ASIO integer overscale saturates correct signed endpoint without wraparound");
        }
      }
    }
    check(validBuffer(256, 16, 1024, -1) && !validBuffer(300, 16, 1024, -1),
          "power-of-two constraint");
    check(validBuffer(192, 64, 512, 64) && !validBuffer(193, 64, 512, 64),
          "granularity constraint");
    check(validBuffer(128, 128, 128, 0) && !validBuffer(256, 128, 128, 0), "fixed-size constraint");
    bool dsd = false;
    try {
      SampleCodec c(ASIOSTDSDInt8LSB1);
    } catch (...) {
      dsd = true;
    }
    check(dsd, "DSD rejected");
    Fake driver;
    DriverEntry entry{"asio:fixture", "Fixture", {}};
    {
      Backend b(&driver, entry);
      check(b.description()["currentSampleRate"] == 48000, "probe current rate");
      check(driver.rateWrites == 0 && driver.starts == 0, "probe never changes rate or starts");
      auto fn = [](void *, float *out, const float *in, unsigned n, unsigned, int latency) {
        for (unsigned s = 0; s < n; ++s) {
          out[s * 4] = in[s * 2] * .5f;
          out[s * 4 + 1] = in[s * 2 + 1];
          out[s * 4 + 2] = .25f;
          out[s * 4 + 3] = 0;
        }
        if (latency != 91)
          std::abort();
      };
      b.open(2, 4, 256, 48000, fn, nullptr);
      check(driver.rateWrites == 0, "same rate not written");
      check(b.latency() == 91, "output-only latency excludes capture");
      for (int c = 2; c < 6; ++c)
        for (auto x : driver.bytes[c][0])
          if (x)
            throw std::runtime_error("initial output not zero");
      check(true, "all initial output buffers zero");
      SampleCodec c(ASIOSTInt32LSB);
      for (int i = 0; i < 256; ++i) {
        c.write(driver.bytes[0][0].data() + i * 4, .5f);
        c.write(driver.bytes[1][0].data() + i * 4, -.5f);
      }
      b.start();
      watch = true;
      for (int i = 0; i < 1000; ++i)
        driver.tick();
      watch = false;
      check(allocations == 0, "1000 realtime callbacks allocate no host memory");
      check(c.read(driver.bytes[2][0].data()) == .25f &&
                c.read(driver.bytes[3][0].data()) == -.5f &&
                c.read(driver.bytes[4][0].data()) == .25f,
            "duplex conversion and four-channel mapping");
      check(b.status()["samplePosition"] == 999 * 256 &&
                b.status()["samplePositionDiscontinuities"] == 0,
            "driver sample clock observed");
      check(driver.cb.asioMessage(kAsioResetRequest, 0, nullptr, nullptr) == 1,
            "reset acknowledged atomically");
      watch = true;
      driver.tick();
      watch = false;
      check(allocations == 0 && driver.stops == 0, "reset callback does not stop or allocate");
      for (int channel = 2; channel < 6; ++channel)
        for (auto x : driver.bytes[channel][0])
          if (x)
            throw std::runtime_error("reset output not silent");
      check(b.restartRequired(), "reset immediately silences all outputs");
      b.stop();
      check(driver.stops == 1, "control thread stops driver");
    }
    check(driver.released, "driver released on close");
    for (long type : {ASIOSTInt32LSB, ASIOSTFloat32LSB, ASIOSTFloat64MSB}) {
      Fake overloadDriver; overloadDriver.sampleType = type;
      Backend b(&overloadDriver, entry);
      b.open(0, 2, 128, 48000,
        [](void *, float *out, const float *, unsigned n, unsigned, int) {
          for(unsigned i=0;i<n;++i){out[i*2]=1.5f;out[i*2+1]=-1.75f;}
        }, nullptr);
      b.start();watch=true;overloadDriver.tick();watch=false;
      check(allocations == 0, "output overload observation adds no callback allocation");
      auto s=b.status();SampleCodec codec(type);
      check(s["integerOutputOverRangeSamples"] == (codec.floating ? 0 : 256),
        "integer output overload counter distinguishes actual integer conversion from float transport");
      check(s["outputSampleFormats"][0]["floatingPoint"]==codec.floating &&
        s["outputSampleFormats"][0]["containerBits"]==codec.bytes*8,
        "status reports actual prepared driver output sample format");
      if(codec.floating)
        check(codec.read(overloadDriver.bytes[0][0].data())==1.5f &&
          codec.read(overloadDriver.bytes[1][0].data())==-1.75f,
          "real fixture callback writes float output beyond nominal full scale unchanged");
      else
        check(codec.raw(overloadDriver.bytes[0][0].data())==2147483647ULL &&
          codec.raw(overloadDriver.bytes[1][0].data())==2147483648ULL,
          "real fixture callback writes saturated integer endpoints");
    }
    Fake failDriver;
    failDriver.failStart = true;
    {
      Backend b(&failDriver, entry);
      b.open(
          0, 2, 128, 48000,
          [](void *, float *out, const float *, unsigned n, unsigned, int) {
            std::fill_n(out, n * 2, 0.f);
          },
          nullptr);
      bool rejected = false;
      try {
        b.start();
      } catch (...) {
        rejected = true;
      }
      check(rejected && failDriver.released && failDriver.stops == 1 && !b.loaded(),
            "failed start releases driver and buffers without requiring another stop");
    }
    Fake rateDriver;
    {
      Backend b(&rateDriver, entry);
      b.open(
          0, 2, 128, 44100,
          [](void *, float *out, const float *, unsigned n, unsigned, int) {
            std::fill_n(out, n * 2, 0.f);
          },
          nullptr);
      check(rateDriver.rateWrites == 1, "explicit rate applied once");
    }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    RackGraphEditAudit::hybrid(check);
    CoUninitialize();
    std::cout << Json{{"passed", true},
                      {"checks", checks},
                      {"hardwareOpened", false},
                      {"assertions", results}}
                     .dump(2)
              << "\n";
    return 0;
  } catch (const std::exception &e) {
    watch = false;
    std::cout << Json{{"passed", false}, {"checks", checks}, {"error", e.what()}}.dump(2) << "\n";
    return 1;
  }
}
