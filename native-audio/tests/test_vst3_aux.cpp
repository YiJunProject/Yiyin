#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
using namespace Steinberg;
using namespace Steinberg::Vst;
static const FUID MatchedId(0x59494445, 0x434B4155, 0x58425553, 0x30303031);
static const FUID FixedId(0x59494445, 0x434B4155, 0x58425553, 0x30303032);
static const FUID AdaptedId(0x59494445, 0x434B4155, 0x58425553, 0x30303033);
static const FUID ControllerId(0x59494445, 0x434B4155, 0x5843544C, 0x30303031);

// Default stereo sidechain is independent from its activation state. This fixture
// models processors that require mono main + mono auxiliary when used on a mono track.
class AuxEffect : public AudioEffect {
  float gain = .5f;
  int auxMode;
  bool auxActive = true;
  int arrangements = 0;
public:
  explicit AuxEffect(int mode) : auxMode(mode) { setControllerClass(ControllerId); }
  static FUnknown *createMatched(void *) { return static_cast<IAudioProcessor *>(new AuxEffect(0)); }
  static FUnknown *createFixed(void *) { return static_cast<IAudioProcessor *>(new AuxEffect(1)); }
  static FUnknown *createAdapted(void *) { return static_cast<IAudioProcessor *>(new AuxEffect(2)); }
  tresult PLUGIN_API initialize(FUnknown *context) override {
    auto result = AudioEffect::initialize(context);
    if (result == kResultOk) {
      addAudioInput(STR16("Main Input"), SpeakerArr::kStereo);
      addAudioInput(STR16("Sidechain"), SpeakerArr::kStereo, kAux, 0);
      addAudioOutput(STR16("Main Output"), SpeakerArr::kStereo);
    }
    return result;
  }
  tresult PLUGIN_API setBusArrangements(SpeakerArrangement *in, int32 ni, SpeakerArrangement *out, int32 no) override {
    ++arrangements;
    if (ni != 2 || no != 1 || (in[0] != SpeakerArr::kMono && in[0] != SpeakerArr::kStereo) || out[0] != in[0]) return kResultFalse;
    const auto wantedAux = auxMode == 1 ? SpeakerArr::kStereo : in[0];
    if (in[1] != wantedAux) {
      if (auxMode == 2) {
        SpeakerArrangement adapted[]{in[0], wantedAux};
        AudioEffect::setBusArrangements(adapted, ni, out, no);
      }
      return kResultFalse;
    }
    return AudioEffect::setBusArrangements(in, ni, out, no);
  }
  tresult PLUGIN_API activateBus(MediaType type, BusDirection dir, int32 index, TBool active) override {
    if (type == kAudio && dir == kInput && index == 1) auxActive = active;
    return AudioEffect::activateBus(type, dir, index, active);
  }
  tresult PLUGIN_API setState(IBStream *stream) override { return stream->read(&gain, 4); }
  tresult PLUGIN_API getState(IBStream *stream) override {
    auto result = stream->write(&gain, 4);
    int32 metadata[]{SpeakerArr::getChannelCount(getAudioInput(0)->getArrangement()), SpeakerArr::getChannelCount(getAudioInput(1)->getArrangement()), arrangements, auxActive ? 1 : 0};
    if (result == kResultOk) result = stream->write(metadata, sizeof(metadata));
    return result;
  }
  tresult PLUGIN_API process(ProcessData &data) override {
    if (data.inputParameterChanges) for (int i = 0; i < data.inputParameterChanges->getParameterCount(); ++i) {
      auto *change = data.inputParameterChanges->getParameterData(i); int32 offset; double value;
      if (change->getParameterId() == 0 && change->getPoint(change->getPointCount() - 1, offset, value) == kResultOk) gain = static_cast<float>(value);
    }
    const auto main = SpeakerArr::getChannelCount(getAudioInput(0)->getArrangement()), aux = SpeakerArr::getChannelCount(getAudioInput(1)->getArrangement());
    if (auxActive || data.numInputs != 2 || data.numOutputs != 1 || data.inputs[0].numChannels != main || data.outputs[0].numChannels != main || data.inputs[1].numChannels != aux) return kResultFalse;
    for (int c = 0; c < aux; ++c) for (int s = 0; s < data.numSamples; ++s) if (data.inputs[1].channelBuffers32[c][s] != 0) return kResultFalse;
    for (int c = 0; c < main; ++c) for (int s = 0; s < data.numSamples; ++s) data.outputs[0].channelBuffers32[c][s] = data.inputs[0].channelBuffers32[c][s] * gain;
    return kResultOk;
  }
};
class AuxController : public EditController {
public:
  static FUnknown *create(void *) { return static_cast<IEditController *>(new AuxController); }
  tresult PLUGIN_API initialize(FUnknown *context) override {
    auto result = EditController::initialize(context);
    if (result == kResultOk) parameters.addParameter(STR16("Gain"), nullptr, 0, .5, ParameterInfo::kCanAutomate, 0);
    return result;
  }
  tresult PLUGIN_API setComponentState(IBStream *stream) override {
    float value; auto result = stream->read(&value, 4);
    if (result == kResultOk) setParamNormalized(0, value); return result;
  }
};
bool InitModule() { return true; }
bool DeinitModule() { return true; }
BEGIN_FACTORY_DEF("Yideck isolated aux test", "https://example.invalid", "none@example.invalid")
DEF_CLASS2(INLINE_UID_FROM_FUID(MatchedId), PClassInfo::kManyInstances, kVstAudioEffectClass,
           "Yideck Matched Auxiliary Test", Vst::kDistributable, "Fx", "1.0.0", kVstVersionString, AuxEffect::createMatched)
DEF_CLASS2(INLINE_UID_FROM_FUID(FixedId), PClassInfo::kManyInstances, kVstAudioEffectClass,
           "Yideck Fixed Stereo Auxiliary Test", Vst::kDistributable, "Fx", "1.0.0", kVstVersionString, AuxEffect::createFixed)
DEF_CLASS2(INLINE_UID_FROM_FUID(AdaptedId), PClassInfo::kManyInstances, kVstAudioEffectClass,
           "Yideck Adapted Auxiliary Test", Vst::kDistributable, "Fx", "1.0.0", kVstVersionString, AuxEffect::createAdapted)
DEF_CLASS2(INLINE_UID_FROM_FUID(ControllerId), PClassInfo::kManyInstances, kVstComponentControllerClass,
           "Yideck Auxiliary Test Controller", 0, "", "1.0.0", kVstVersionString, AuxController::create)
END_FACTORY
