#pragma once

#include <algorithm>
#include <cstddef>
#include <span>
#include <vector>

namespace aevocis::core {

class AudioBuffer {
public:
    explicit AudioBuffer(std::size_t capacity) : samples_(), capacity_(capacity) { samples_.reserve(capacity); }

    [[nodiscard]] bool append(std::span<const float> samples) {
        if (samples.size() > capacity_ - samples_.size()) {
            return false;
        }
        samples_.insert(samples_.end(), samples.begin(), samples.end());
        return true;
    }

    [[nodiscard]] const std::vector<float>& samples() const noexcept { return samples_; }
    [[nodiscard]] std::size_t size() const noexcept { return samples_.size(); }
    void clear() noexcept { samples_.clear(); }

private:
    std::vector<float> samples_;
    std::size_t capacity_;
};

}  // namespace aevocis::core
