#include "plugins.h"
#include "protocol.h"
#include <iostream>
int wmain(int argc, wchar_t **argv) {
  ProtocolOutput protocol;
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
  CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  try {
    if (argc != 2)
      throw std::runtime_error("Usage: Yideck.Audio.Scan.exe <plugin path>");
    initializePlugins();
    auto found = scanPlugin(utf8(argv[1]));
    if (found.empty())
      throw std::runtime_error("Plugin factory exposed no audio effect "
                               "classes; inspect scanner diagnostics");
    protocol.write(Json{{"ok", true}, {"plugins", found}});
    CoUninitialize();
    return 0;
  } catch (const std::exception &e) {
    protocol.write(Json{{"ok", false}, {"error", e.what()}});
    CoUninitialize();
    return 1;
  }
}
