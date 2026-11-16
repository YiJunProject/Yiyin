#pragma once
#include "json.hpp"
#include <algorithm>
#include <atomic>
#include <memory>
#include <objbase.h>
#include <string>
#include <stdexcept>
#include <vector>
#include <windows.h>
using Json = nlohmann::json;
std::wstring wide(const std::string &s);
std::string utf8(const std::wstring &s);
std::string base64(const void *data, size_t size);
std::vector<unsigned char> unbase64(const std::string &s);
struct Plugin {
  std::string id;
  HWND window = nullptr;
  HWND editorContent = nullptr;
  int editorWidth = 640, editorHeight = 480;
  bool fixedEditor = true, editorLayout = false;
  HWND editorOwner = nullptr;
  virtual ~Plugin() = default;
  virtual void process(float **in, float **out, int frames, uint64_t position) = 0;
  virtual int latency() const = 0;
  virtual Json state() = 0;
  virtual void restore(const Json &) = 0;
  virtual void parameter(uint32_t, double) = 0;
  virtual Json describe() = 0;
  virtual void editor(bool) = 0;
  virtual void resized(int, int) {}
  virtual void dpiChanged(unsigned) {}
  virtual void idle() {}
  virtual bool runningStateSafe() const { return false; }
  virtual void prepareRunningState() {}
  virtual void flushParametersStopped() {}
  virtual bool needsRestart() { return false; }
  virtual void acknowledgeRestart() {}
};
struct PluginFailure : std::runtime_error {
  std::string instanceId;
  PluginFailure(const std::string &id, const std::string &message)
      : std::runtime_error(message), instanceId(id) {}
};
void setPluginEditorOwner(Plugin &, HWND owner);
bool activatePluginEditor(Plugin &);
void initializePlugins();
std::unique_ptr<Plugin> makePlugin(const Json &, int channels, double rate, int block);
Json scanPlugin(const std::string &path);
