#include "aevocis/core/recognizer.hpp"

namespace aevocis::core {

RecognitionResult UnavailableRecognizer::recognize(std::span<const float>, std::uint32_t, std::stop_token stop) const {
    if (stop.stop_requested()) {
        return {"", ErrorCode::Cancelled, true};
    }
    return {"", ErrorCode::RecognitionUnavailable, true};
}

}  // namespace aevocis::core
