#include "aevocis/platform/windows/update_manager.hpp"

#include "aevocis/platform/windows/messages.hpp"
#include "aevocis/platform/windows/storage.hpp"

#include <bcrypt.h>
#include <shellapi.h>
#include <winhttp.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <memory>
#include <sstream>
#include <thread>
#include <vector>

namespace aevocis::platform::windows {

namespace {

struct HttpResponse {
    std::string body;
    DWORD status{};
};

[[nodiscard]] std::wstring ascii_to_wide(std::string_view value) {
    return std::wstring(value.begin(), value.end());
}

[[nodiscard]] std::string wide_to_ascii(std::wstring_view value) {
    std::string result;
    result.reserve(value.size());
    for (const wchar_t byte : value) {
        if (byte >= 0 && byte <= 0x7F) {
            result.push_back(static_cast<char>(byte));
        }
    }
    return result;
}

[[nodiscard]] std::string json_string(std::string_view json, std::string_view key) {
    const std::string needle = "\"" + std::string(key) + "\":\"";
    const std::size_t begin = json.find(needle);
    if (begin == std::string_view::npos) {
        return {};
    }
    const std::size_t value_begin = begin + needle.size();
    std::size_t value_end = value_begin;
    bool escaped = false;
    for (; value_end < json.size(); ++value_end) {
        if (escaped) {
            escaped = false;
        } else if (json[value_end] == '\\') {
            escaped = true;
        } else if (json[value_end] == '"') {
            break;
        }
    }
    return std::string(json.substr(value_begin, value_end - value_begin));
}

struct UpdateAsset {
    std::string url;
    std::string digest;
};

[[nodiscard]] UpdateAsset installer_asset(std::string_view json) {
    constexpr std::string_view name_needle = "\"name\":\"";
    constexpr std::string_view installer_suffix = "-Setup.exe";
    std::size_t cursor = 0;
    while ((cursor = json.find(name_needle, cursor)) != std::string_view::npos) {
        const std::size_t name_begin = cursor + name_needle.size();
        const std::size_t name_end = json.find('"', name_begin);
        if (name_end == std::string_view::npos) return {};
        const std::string_view name = json.substr(name_begin, name_end - name_begin);
        if (name.size() >= installer_suffix.size() &&
            name.compare(name.size() - installer_suffix.size(), installer_suffix.size(), installer_suffix) == 0) {
            const std::size_t object_end = json.find('}', name_end);
            if (object_end == std::string_view::npos) return {};
            const std::string_view object = json.substr(cursor, object_end - cursor + 1);
            return {json_string(object, "browser_download_url"), json_string(object, "digest")};
        }
        cursor = name_end + 1;
    }
    return {};
}

[[nodiscard]] HttpResponse https_get(std::wstring_view host, std::wstring_view path) noexcept {
    HttpResponse response;
    HINTERNET session = WinHttpOpen(L"Aevocis/0.2.1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (session == nullptr) {
        return response;
    }
    (void)WinHttpSetTimeouts(session, 3000, 3000, 5000, 5000);
    HINTERNET connection = WinHttpConnect(session, host.data(), INTERNET_DEFAULT_HTTPS_PORT, 0);
    if (connection == nullptr) {
        WinHttpCloseHandle(session);
        return response;
    }
    HINTERNET request = WinHttpOpenRequest(connection, L"GET", path.data(), nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (request == nullptr) {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return response;
    }
    const wchar_t headers[] = L"Accept: application/vnd.github+json\r\nUser-Agent: Aevocis\r\n";
    if (WinHttpSendRequest(request, headers, ARRAYSIZE(headers) - 1, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) == FALSE ||
        WinHttpReceiveResponse(request, nullptr) == FALSE) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return response;
    }
    DWORD status_size = sizeof(response.status);
    (void)WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                              WINHTTP_HEADER_NAME_BY_INDEX, &response.status, &status_size, WINHTTP_NO_HEADER_INDEX);
    for (;;) {
        DWORD available = 0;
        if (WinHttpQueryDataAvailable(request, &available) == FALSE || available == 0) {
            break;
        }
        std::string chunk(available, '\0');
        DWORD read = 0;
        if (WinHttpReadData(request, chunk.data(), available, &read) == FALSE) {
            response.body.clear();
            break;
        }
        chunk.resize(read);
        response.body += chunk;
    }
    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return response;
}

[[nodiscard]] std::string sha256_file(const std::filesystem::path& path) noexcept {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::string result;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) {
        return result;
    }
    DWORD object_size = 0;
    DWORD result_size = 0;
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size),
                          &result_size, 0) != 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return result;
    }
    std::vector<UCHAR> object(object_size);
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) != 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return result;
    }
    std::ifstream stream(path, std::ios::binary);
    std::array<char, 64 * 1024> buffer{};
    while (stream.good()) {
        stream.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const std::streamsize read = stream.gcount();
        if (read > 0 && BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(read), 0) != 0) {
            BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algorithm, 0);
            return {};
        }
    }
    std::array<UCHAR, 32> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) == 0) {
        static constexpr char hex[] = "0123456789abcdef";
        result.reserve(64);
        for (const UCHAR byte : digest) {
            result.push_back(hex[(byte >> 4U) & 0x0FU]);
            result.push_back(hex[byte & 0x0FU]);
        }
    }
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return result;
}

[[nodiscard]] bool download_asset(std::string_view url, const std::filesystem::path& destination) noexcept {
    constexpr std::string_view prefix = "https://github.com/Aevorine/Aevocis";
    if (url.rfind(prefix, 0) != 0) {
        return false;
    }
    const std::wstring path = ascii_to_wide(url.substr(prefix.size()));
    HINTERNET session = WinHttpOpen(L"Aevocis/0.2.1", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (session == nullptr) return false;
    (void)WinHttpSetTimeouts(session, 3000, 3000, 15000, 15000);
    HINTERNET connection = WinHttpConnect(session, L"github.com", INTERNET_DEFAULT_HTTPS_PORT, 0);
    HINTERNET request = connection != nullptr
                            ? WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER,
                                                 WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE)
                            : nullptr;
    const bool sent = request != nullptr && WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                                               WINHTTP_NO_REQUEST_DATA, 0, 0, 0) != FALSE &&
                      WinHttpReceiveResponse(request, nullptr) != FALSE;
    if (!sent) {
        if (request != nullptr) WinHttpCloseHandle(request);
        if (connection != nullptr) WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return false;
    }
    const auto temporary = destination.parent_path() / (destination.filename().wstring() + L".download");
    std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
    if (!stream) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return false;
    }
    bool success = true;
    for (;;) {
        DWORD available = 0;
        if (WinHttpQueryDataAvailable(request, &available) == FALSE || available == 0) break;
        std::vector<char> buffer(available);
        DWORD read = 0;
        if (WinHttpReadData(request, buffer.data(), available, &read) == FALSE) {
            success = false;
            break;
        }
        stream.write(buffer.data(), static_cast<std::streamsize>(read));
        if (!stream) {
            success = false;
            break;
        }
    }
    stream.close();
    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    if (!success || !MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        (void)DeleteFileW(temporary.c_str());
        return false;
    }
    return true;
}

[[nodiscard]] int version_number(std::string_view value) noexcept {
    int number = 0;
    int component = 0;
    bool in_component = false;
    for (const char byte : value) {
        if (std::isdigit(static_cast<unsigned char>(byte)) != 0) {
            component = std::min(component * 10 + (byte - '0'), 999);
            in_component = true;
        } else if (in_component) {
            number = std::min(number * 1000 + component, 2'000'000'000);
            component = 0;
            in_component = false;
        }
    }
    if (in_component) {
        number = std::min(number * 1000 + component, 2'000'000'000);
    }
    return number;
}

}  // namespace

void UpdateManager::check_and_install_async(HWND owner, std::wstring current_version, std::wstring executable_path) noexcept {
    std::thread([owner, current_version = std::move(current_version), executable_path = std::move(executable_path)] {
        check_and_install(owner, current_version, executable_path);
    }).detach();
}

void UpdateManager::check_and_install(HWND owner, const std::wstring& current_version, const std::wstring& executable_path) noexcept {
    const HttpResponse response = https_get(L"api.github.com", L"/repos/Aevorine/Aevocis/releases/latest");
    if (response.status != 200) {
        MessageBoxW(owner, L"暂时无法检查更新。", L"Aevocis", MB_OK | MB_ICONINFORMATION);
        return;
    }
    const std::string tag = json_string(response.body, "tag_name");
    const UpdateAsset asset = installer_asset(response.body);
    const std::string current = wide_to_ascii(current_version);
    if (tag.empty() || version_number(tag) <= version_number(current)) {
        MessageBoxW(owner, L"当前已是最新版本。", L"Aevocis", MB_OK | MB_ICONINFORMATION);
        return;
    }
    if (asset.url.empty() || asset.digest.rfind("sha256:", 0) != 0) {
        MessageBoxW(owner, L"新版本缺少可验证的安装包。", L"Aevocis", MB_OK | MB_ICONWARNING);
        return;
    }
    const int choice = MessageBoxW(owner, L"发现新版本，是否立即下载并安装？", L"Aevocis 更新", MB_YESNO | MB_ICONINFORMATION);
    if (choice != IDYES) return;
    const auto update_directory = Storage::data_directory() / L"updates";
    std::error_code error;
    std::filesystem::create_directories(update_directory, error);
    const auto installer = update_directory / L"Aevocis-Setup.exe";
    if (!download_asset(asset.url, installer) || sha256_file(installer) != asset.digest.substr(7)) {
        (void)DeleteFileW(installer.c_str());
        MessageBoxW(owner, L"安装包下载或校验失败。", L"Aevocis", MB_OK | MB_ICONERROR);
        return;
    }
    const HINSTANCE launched = ShellExecuteW(nullptr, L"open", installer.c_str(), nullptr,
                                             std::filesystem::path(executable_path).parent_path().c_str(), SW_SHOWNORMAL);
    if (reinterpret_cast<INT_PTR>(launched) <= 32) {
        MessageBoxW(owner, L"安装程序启动失败。", L"Aevocis", MB_OK | MB_ICONERROR);
        return;
    }
    (void)PostMessageW(owner, kUpdateQuitMessage, 0, 0);
}

}  // namespace aevocis::platform::windows
