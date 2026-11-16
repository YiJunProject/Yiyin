#pragma once
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>

// One audio producer. Observation only: no limiter or sample mutation.
// Control resets only while stopped (configure); counters count channel samples.
struct OutputSignal {
  std::atomic<float> lastBlockPeak{0}, peakSinceReset{0};
  std::atomic<uint64_t> overRangeSamples{0}, nonFiniteSamples{0}, framesObserved{0};
  void reset() noexcept {
    lastBlockPeak = 0; peakSinceReset = 0;
    overRangeSamples = 0; nonFiniteSamples = 0; framesObserved = 0;
  }
  void observe(const float *samples, unsigned frames, unsigned channels) noexcept {
    float peak = 0;
    uint64_t over = 0, invalid = 0;
    for (size_t i = 0; i < size_t(frames) * channels; ++i) {
      const auto sample = samples[i];
      if (!std::isfinite(sample)) { ++invalid; continue; }
      const auto magnitude = std::abs(sample);
      peak = std::max(peak, magnitude);
      over += magnitude > 1.f;
    }
    lastBlockPeak.store(peak, std::memory_order_relaxed);
    // Single producer, and reset is stopped-only: no CAS retry is needed.
    if (peak > peakSinceReset.load(std::memory_order_relaxed))
      peakSinceReset.store(peak, std::memory_order_relaxed);
    overRangeSamples.fetch_add(over, std::memory_order_relaxed);
    nonFiniteSamples.fetch_add(invalid, std::memory_order_relaxed);
    framesObserved.fetch_add(frames, std::memory_order_relaxed);
  }
};
