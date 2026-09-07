#include "aevocis/platform/windows/crash_reporter.hpp"

#include "aevocis/platform/windows/storage.hpp"

#include <windows.h>

#include <dbghelp.h>
#include <filesystem>

namespace aevocis::platform::windows {

namespace {

LONG WINAPI unhandled_exception(EXCEPTION_POINTERS* exception) noexcept {
    const auto directory = Storage::data_directory() / L"crash-dumps";
    (void)CreateDirectoryW(Storage::data_directory().c_str(), nullptr);
    (void)CreateDirectoryW(directory.c_str(), nullptr);
    SYSTEMTIME time{};
    GetLocalTime(&time);
    wchar_t filename[128]{};
    (void)swprintf_s(filename, L"crash-%04u%02u%02u-%02u%02u%02u.dmp", time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
                     time.wSecond);
    const auto path = directory / filename;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        HMODULE dbghelp = LoadLibraryW(L"dbghelp.dll");
        if (dbghelp != nullptr) {
            using WriteDump = BOOL(WINAPI*)(HANDLE, DWORD, HANDLE, MINIDUMP_TYPE, const MINIDUMP_EXCEPTION_INFORMATION*,
                                            const MINIDUMP_USER_STREAM_INFORMATION*, const MINIDUMP_CALLBACK_INFORMATION*);
            const auto writer = reinterpret_cast<WriteDump>(GetProcAddress(dbghelp, "MiniDumpWriteDump"));
            if (writer != nullptr) {
                MINIDUMP_EXCEPTION_INFORMATION info{GetCurrentThreadId(), exception, FALSE};
                (void)writer(GetCurrentProcess(), GetCurrentProcessId(), file, MiniDumpWithIndirectlyReferencedMemory, &info, nullptr, nullptr);
            }
            (void)FreeLibrary(dbghelp);
        }
        (void)CloseHandle(file);
    }
    return EXCEPTION_EXECUTE_HANDLER;
}

}  // namespace

void CrashReporter::install() noexcept { (void)SetUnhandledExceptionFilter(unhandled_exception); }

}  // namespace aevocis::platform::windows
