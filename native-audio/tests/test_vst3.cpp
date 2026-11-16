#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
#include <array>
#ifdef YIDECK_EDITOR_FIXTURE
#include "test_vst3_view.h"
#endif
using namespace Steinberg;
using namespace Steinberg::Vst;
static const FUID EffectId(0x59494445, 0x434B4658, 0x54455354, 0x30303031);
static const FUID ControllerId(0x59494445, 0x434B4354, 0x54455354, 0x30303031);
class TestEffect : public AudioEffect {
  float gain = .5f;
  std::array<std::array<float, 17>, 2> delay{};
  int pos = 0;

public:
  TestEffect() { setControllerClass(ControllerId); }
  static FUnknown *create(void *) { return static_cast<IAudioProcessor *>(new TestEffect); }
  tresult PLUGIN_API initialize(FUnknown *c) override {
    auto r = AudioEffect::initialize(c);
    if (r == kResultOk) {
      addAudioInput(STR16("Stereo In"), SpeakerArr::kStereo);
      addAudioOutput(STR16("Stereo Out"), SpeakerArr::kStereo);
    }
    return r;
  }
  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *in, int32 ni, SpeakerArrangement *out,
                                        int32 no) override {
    if (ni != 1 || no != 1 || in[0] != SpeakerArr::kStereo || out[0] != SpeakerArr::kStereo)
      return kResultFalse;
    return AudioEffect::setBusArrangements(in, ni, out, no);
  }
  uint32 PLUGIN_API getLatencySamples() override { return 17; }
  tresult PLUGIN_API setState(IBStream *s) override { return s->read(&gain, 4); }
  tresult PLUGIN_API getState(IBStream *s) override { return s->write(&gain, 4); }
  tresult PLUGIN_API process(ProcessData &d) override {
    if (d.inputParameterChanges)
      for (int i = 0; i < d.inputParameterChanges->getParameterCount(); ++i) {
        auto *q = d.inputParameterChanges->getParameterData(i);
        int32 offset;
        double value;
        if (q->getParameterId() == 0 &&
            q->getPoint(q->getPointCount() - 1, offset, value) == kResultOk)
          gain = (float)value;
      }
    if (d.numInputs < 1 || d.numOutputs < 1)
      return kResultOk;
    for (int s = 0; s < d.numSamples; ++s) {
      for (int c = 0; c < 2; ++c) {
        d.outputs[0].channelBuffers32[c][s] = delay[c][pos] * gain;
        delay[c][pos] = d.inputs[0].channelBuffers32[c][s];
      }
      pos = (pos + 1) % 17;
    }
    return kResultOk;
  }
};
class TestController : public EditController {
public:
#ifdef YIYIN_SINGLE_CONNECT_FIXTURE
  bool connectedOnce = false;
  tresult PLUGIN_API connect(IConnectionPoint *other) override {
    if (connectedOnce) return kResultFalse;
    connectedOnce = true;
    return EditController::connect(other);
  }
#endif
#ifdef YIDECK_EDITOR_FIXTURE
  IPlugView *PLUGIN_API createView(FIDString name) override { return name && !strcmp(name, ViewType::kEditor) ? new EditorFixtureView : nullptr; }
#endif
  static FUnknown *create(void *) { return static_cast<IEditController *>(new TestController); }
  tresult PLUGIN_API initialize(FUnknown *c) override {
    auto r = EditController::initialize(c);
    if (r == kResultOk)
      parameters.addParameter(STR16("Gain"), nullptr, 0, .5, ParameterInfo::kCanAutomate, 0);
    return r;
  }
  tresult PLUGIN_API setComponentState(IBStream *s) override {
    float gain;
    auto r = s->read(&gain, 4);
    if (r == kResultOk)
      setParamNormalized(0, gain);
    return r;
  }
};
bool InitModule() { return true; }
bool DeinitModule() { return true; }
BEGIN_FACTORY_DEF("Yideck isolated test", "https://example.invalid", "none@example.invalid")
DEF_CLASS2(INLINE_UID_FROM_FUID(EffectId), PClassInfo::kManyInstances, kVstAudioEffectClass,
           "Yideck Test VST3 Delay Gain", Vst::kDistributable, "Fx", "1.0.0", kVstVersionString,
           TestEffect::create)
DEF_CLASS2(INLINE_UID_FROM_FUID(ControllerId), PClassInfo::kManyInstances,
           kVstComponentControllerClass, "Yideck Test Controller", 0, "", "1.0.0",
           kVstVersionString, TestController::create)
END_FACTORY
