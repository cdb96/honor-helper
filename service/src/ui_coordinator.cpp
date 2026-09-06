// UiCoordinator port: same message names ("HonorHelperShowUi/HideUi/QuitUi"),
// same HWND_BROADCAST delivery, same launch-next-to-service + 2s quit grace.
#include "ui_coordinator.h"

#include <windows.h>

namespace svc {

UiCoordinator::UiCoordinator() {
    msgShow_ = RegisterWindowMessageW(L"HonorHelperShowUi");
    msgHide_ = RegisterWindowMessageW(L"HonorHelperHideUi");
    msgQuit_ = RegisterWindowMessageW(L"HonorHelperQuitUi");
    wchar_t path[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring exe = n ? std::wstring(path, n) : L"";
    size_t slash = exe.find_last_of(L"\\/");
    std::wstring dir = slash == std::wstring::npos ? L"" : exe.substr(0, slash);
    uiExe_ = dir.empty() ? L"honor-helper.exe" : dir + L"\\honor-helper.exe";
}

UiCoordinator::~UiCoordinator() {
    if (child_) {
        CloseHandle(child_);
        child_ = nullptr;
    }
}

bool UiCoordinator::pollRunning() const {
    if (!child_)
        return false;
    DWORD code = 0;
    if (!GetExitCodeProcess(child_, &code))
        return false;
    return code == STILL_ACTIVE;
}

bool UiCoordinator::isRunning() const {
    // Mutable refresh through a const API (same best-effort spirit as the C#).
    UiCoordinator* self = const_cast<UiCoordinator*>(this);
    if (self->child_ && !self->pollRunning()) {
        CloseHandle(self->child_);
        self->child_ = nullptr;
        self->childPid_ = 0;
        return false;
    }
    return self->child_ != nullptr;
}

void UiCoordinator::broadcast(UINT msg) const {
    PostMessageW(HWND_BROADCAST, msg, 0, 0);
}

void UiCoordinator::show() {
    if (isRunning()) {
        broadcast(msgShow_);
        return;
    }
    DWORD attr = GetFileAttributesW(uiExe_.c_str());
    if (attr == INVALID_FILE_ATTRIBUTES)
        return;
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    std::wstring cmd = L"\"" + uiExe_ + L"\"";
    if (CreateProcessW(uiExe_.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0,
                       nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hThread);
        if (child_)
            CloseHandle(child_);
        child_ = pi.hProcess;
        childPid_ = pi.dwProcessId;
    }
}

void UiCoordinator::hide() { broadcast(msgHide_); }

void UiCoordinator::toggle() {
    if (isRunning())
        broadcast(msgHide_);
    else
        show();
}

void UiCoordinator::quit() {
    broadcast(msgQuit_);
    if (child_ && pollRunning()) {
        DWORD w = WaitForSingleObject(child_, 2000);
        if (w == WAIT_TIMEOUT)
            TerminateProcess(child_, 0);
    }
    if (child_) {
        CloseHandle(child_);
        child_ = nullptr;
        childPid_ = 0;
    }
}

}  // namespace svc
