#pragma once
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <thread>

// One audio producer and one control-thread reader. The reader changes banks,
// waits only on the control thread for a claimed bank, then takes a consistent
// peak/square-sum/frame-count interval. Audio never waits for the reader.
struct IntervalMeter {
  struct Snapshot {
    float peakLeft = 0, peakRight = 0;
    double squareLeft = 0, squareRight = 0;
    uint64_t sampleCount = 0;
    double rmsLeft() const { return sampleCount ? std::sqrt(squareLeft / sampleCount) : 0; }
    double rmsRight() const { return sampleCount ? std::sqrt(squareRight / sampleCount) : 0; }
  };
private:
  struct Bank { std::atomic<unsigned> writers{0}; Snapshot value; } banks[2];
  std::atomic<unsigned> active{0};
public:
  // Non-consuming last-block values preserve the existing status contract.
  std::atomic<float> left{0}, right{0};
  static_assert(std::atomic<unsigned>::is_always_lock_free && std::atomic<float>::is_always_lock_free);
  void publish(float peakLeft, float peakRight, double squareLeft, double squareRight,
               uint64_t frames) noexcept {
    left.store(peakLeft, std::memory_order_relaxed);
    right.store(peakRight, std::memory_order_relaxed);
    unsigned index;
    for (;;) {
      index = active.load();
      banks[index].writers.fetch_add(1);
      if (active.load() == index) break;
      banks[index].writers.fetch_sub(1);
    }
    auto &v = banks[index].value;
    v.peakLeft = std::max(v.peakLeft, peakLeft);
    v.peakRight = std::max(v.peakRight, peakRight);
    v.squareLeft += squareLeft;
    v.squareRight += squareRight;
    v.sampleCount += frames;
    banks[index].writers.fetch_sub(1);
  }
  Snapshot take() {
    auto previous = active.fetch_xor(1);
    while (banks[previous].writers.load()) std::this_thread::yield();
    auto result = banks[previous].value;
    banks[previous].value = {};
    return result;
  }
};
