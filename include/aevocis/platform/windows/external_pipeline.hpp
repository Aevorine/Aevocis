#pragma once

#include <cstdint>
#include <optional>
#include <string>

namespace aevocis::platform::windows {

// F1: a user-opted-in post-processing hook. Deliberately a subprocess pipe (stdin -> stdout),
// not a loaded DLL/plugin -- a bad DLL plugin runs inside this process with full access to
// everything it has (injection, settings, history); a bad subprocess is a normal OS process
// with only what its own user account can already do, killable on timeout, no different in
// principle from a user running their own script. The user still has to explicitly type an exe
// path into settings for this to ever run anything -- there's no directory auto-scan / auto-
// discovery of "plugins" to load unprompted.
class ExternalPipeline {
public:
    // Returns std::nullopt on any failure (exe missing, non-zero timeout exceeded, non-zero
    // exit code) so a broken external tool degrades to "text passes through unmodified" at the
    // call site, never to a crash or a silently corrupted transcript.
    [[nodiscard]] static std::optional<std::string> run(const std::wstring& executable_path, const std::string& input_utf8,
                                                         std::uint32_t timeout_ms = 3000) noexcept;
};

}  // namespace aevocis::platform::windows
