#pragma once
#include "plugins.h"
#include <cstdio>
#include <io.h>

// Third-party modules sometimes print directly to stdout. Preserve the parent's
// protocol pipe separately and redirect all normal plugin output to
// diagnostics.
class ProtocolOutput {
  HANDLE pipe = nullptr;

public:
  ProtocolOutput() {
    auto process = GetCurrentProcess();
    if (!DuplicateHandle(process, GetStdHandle(STD_OUTPUT_HANDLE), process, &pipe, 0, FALSE,
                         DUPLICATE_SAME_ACCESS))
      throw std::runtime_error("Cannot preserve protocol output");
    _dup2(_fileno(stderr), _fileno(stdout));
    SetStdHandle(STD_OUTPUT_HANDLE, GetStdHandle(STD_ERROR_HANDLE));
  }
  ~ProtocolOutput() {
    if (pipe)
      CloseHandle(pipe);
  }
  void write(const Json &value) {
    auto text = value.dump(-1, ' ', false, Json::error_handler_t::replace) + "\n";
    size_t sent = 0;
    while (sent < text.size()) {
      DWORD n = 0;
      if (!WriteFile(pipe, text.data() + sent, (DWORD)std::min<size_t>(text.size() - sent, 1048576),
                     &n, nullptr) ||
          !n)
        throw std::runtime_error("Protocol pipe closed");
      sent += n;
    }
  }
};
