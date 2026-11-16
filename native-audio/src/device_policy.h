#pragma once
#include <stdexcept>
#include <string>
inline void validateDeviceGroup(const std::string &input, const std::string &output) {
  if (input.empty() || output.empty())
    throw std::runtime_error("Cannot establish the physical device container for both endpoints; cross-device aggregation is not supported");
  if (input != output)
    throw std::runtime_error("Input and output belong to different physical devices; choose endpoints on the same audio interface");
}
