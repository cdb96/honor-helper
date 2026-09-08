// TriggerEngine port: fixed 3s scan, name-based edges, actions applied on
// worker threads so one slow WMI write never stalls the scan loop.
#include "trigger_engine.h"

#include <windows.h>

#include <psapi.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cwctype>

#include "hardware.h"
#include "stores.h"

#pragma comment(lib, "psapi.lib")

namespace svc {

TriggerEngine::TriggerEngine(TriggerStore& store) : store_(store) {}
TriggerEngine::~TriggerEngine() { stop(); }

void TriggerEngine::start() {
    stop_.store(false);
    if (wake_ == nullptr)
        wake_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    else
        ResetEvent(wake_);
    if (thread_ == nullptr)
        thread_ = CreateThread(nullptr, 0, &TriggerEngine::ThreadMain, this, 0, nullptr);
}

void TriggerEngine::stop() {
    stop_.store(true);
    if (wake_ != nullptr)
        SetEvent(wake_);
    if (thread_ != nullptr) {
        DWORD rc = WaitForSingleObject(thread_, 5000);
        if (rc == WAIT_TIMEOUT)
            TerminateThread(thread_, 0);
        CloseHandle(thread_);
        thread_ = nullptr;
    }
    if (wake_ != nullptr) {
        CloseHandle(wake_);
        wake_ = nullptr;
    }
}

DWORD WINAPI TriggerEngine::ThreadMain(LPVOID self) {
    ((TriggerEngine*)self)->loop();
    return 0;
}

void TriggerEngine::loop() {
    for (;;) {
        if (stop_.load())
            break;
        DWORD w = WaitForSingleObject(wake_, 3000);
        if (w != WAIT_TIMEOUT || stop_.load())
            break;
        try {
            scanOnce();
        } catch (...) {
            // never let the loop die
        }
    }
}

static std::wstring ToLower(std::wstring s) {
    for (auto& ch : s)
        ch = (wchar_t)std::towlower(ch);
    return s;
}

static std::wstring StemLower(const std::string& pathU8) {
    if (pathU8.empty())
        return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, pathU8.data(), (int)pathU8.size(),
                                nullptr, 0);
    std::wstring w((size_t)(n > 0 ? n : 0), L'\0');
    if (n > 0)
        MultiByteToWideChar(CP_UTF8, 0, pathU8.data(), (int)pathU8.size(), w.data(), n);
    size_t slash = w.find_last_of(L"\\/");  // strip directory
    std::wstring base = slash == std::wstring::npos ? w : w.substr(slash + 1);
    size_t dot = base.find_last_of(L'.');  // strip extension
    if (dot != std::wstring::npos)
        base.resize(dot);
    return ToLower(base);
}

void TriggerEngine::scanOnce() {
    std::vector<Trigger> triggers = store_.snapshot();
    if (triggers.empty()) {
        running_.clear();
        return;
    }

    // Enumerate current process exe base names (lower-cased, no extension).
    std::set<std::wstring> now;
    {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap != INVALID_HANDLE_VALUE) {
            PROCESSENTRY32W pe{};
            pe.dwSize = sizeof(pe);
            if (Process32FirstW(snap, &pe)) {
                do {
                    std::wstring exe = pe.szExeFile ? pe.szExeFile : L"";
                    size_t dot = exe.find_last_of(L'.');
                    if (dot != std::wstring::npos)
                        exe.resize(dot);
                    if (!exe.empty())
                        now.insert(ToLower(exe));
                } while (Process32NextW(snap, &pe));
            }
            CloseHandle(snap);
        } else {
            return;
        }
    }

    for (const auto& t : triggers) {
        if (!t.dto.enabled)
            continue;
        std::wstring name = StemLower(t.dto.path);
        if (name.empty())
            continue;
        bool was = running_.count(name) != 0;
        bool isRunning = now.count(name) != 0;
        if (isRunning && !was && t.dto.openAction != "none") {
            std::wstring n = name;
            // Fire-and-forget worker (mirrors the C# Task.Run per edge).
            struct Ctx {
                TriggerStore* store;
                std::wstring exe;
            };
            Ctx* c = new Ctx{&store_, n};
            HANDLE th = CreateThread(
                nullptr, 0,
                [](LPVOID p) -> DWORD {
                    Ctx* c = (Ctx*)p;
                    RunTrigger(*c->store, c->exe, true);
                    delete c;
                    return 0;
                },
                c, 0, nullptr);
            if (th)
                CloseHandle(th);
            else
                delete c;
        } else if (!isRunning && was && t.dto.closeAction != "none") {
            std::wstring n = name;
            struct Ctx {
                TriggerStore* store;
                std::wstring exe;
            };
            Ctx* c = new Ctx{&store_, n};
            HANDLE th = CreateThread(
                nullptr, 0,
                [](LPVOID p) -> DWORD {
                    Ctx* c = (Ctx*)p;
                    RunTrigger(*c->store, c->exe, false);
                    delete c;
                    return 0;
                },
                c, 0, nullptr);
            if (th)
                CloseHandle(th);
            else
                delete c;
        }
    }

    running_.swap(now);
}

// Apply one open/close edge. Matches the old MainWindow.RunTriggerAsync:
// SetPerfMode first, then -- only on open+beast -- PPM after a settle
// delay (EC applies the mode asynchronously; writing PPM too early is eaten
// by the mode default). The old code waited 600ms, retried once after 400ms.
void TriggerEngine::RunTrigger(const TriggerStore& store, std::wstring exeBaseLower,
                               bool opening) {
    bool stateChanged = false;
    std::vector<Trigger> triggers = store.snapshot();
    for (const auto& t : triggers) {
        if (!t.dto.enabled)
            continue;
        if (StemLower(t.dto.path) != exeBaseLower)
            continue;
        const std::string& modeId = opening ? t.dto.openAction : t.dto.closeAction;
        int mode = 0;
        bool modeOk = false;
        if (ProfileMode(modeId, mode))
            modeOk = HardwareSetPerfMode(mode);
        stateChanged = stateChanged || modeOk;
        if (opening && modeOk && mode == kBeastPerfMode &&
            t.dto.openPpm >= 0 && t.dto.openPpm <= 4) {
            Sleep(600);
            bool ppmOk = HardwareSetPpm(t.dto.openPpm);
            if (!ppmOk) {
                Sleep(400);
                ppmOk = HardwareSetPpm(t.dto.openPpm);
            }
            stateChanged = stateChanged || ppmOk;
        }
        const std::string& tpId = opening ? t.dto.openTouchpad : t.dto.closeTouchpad;
        if (tpId == "tp_on" || tpId == "tp_off")
            stateChanged = HardwareSetTouchpad(tpId == "tp_on") || stateChanged;
        if (t.dto.gpuCoreMhz != 0 || t.dto.gpuMemMhz != 0) {
            int core = opening ? t.dto.gpuCoreMhz : 0;
            int mem = opening ? t.dto.gpuMemMhz : 0;
            // Mirror the C# guard: only non-zero offsets are written, so a
            // close edge with (core=300, mem=0) resets core only when the
            // open offset was non-zero... exact same branch shape as C#.
            if (core != 0)
                HardwareOcApply(0 /*HC_CLOCK_GRAPHICS*/, core);
            if (mem != 0)
                HardwareOcApply(4 /*HC_CLOCK_MEMORY*/, mem);
        }
        if (!opening && t.dto.gpuFixOnExit) {
            GpuFixResult r{};
            HardwareGpuFix(false, r);
        }
    }
    if (stateChanged) {
        static const UINT stateChangedMessage =
            RegisterWindowMessageW(L"HonorHelperStateChanged");
        PostMessageW(HWND_BROADCAST, stateChangedMessage, 0, 0);
    }
}

}  // namespace svc
