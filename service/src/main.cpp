// honor-helper-service (pure C++): owns all hardware access (honor_core TU
// linked in statically), serves the UI over a medium-integrity named pipe,
// runs the program-linkage engine, and hosts the tray icon.
//
// GUI subsystem (no console): diagnostics go to %LOCALAPPDATA%\honor-helper\.
//
// Usage:
//   (no args / --background)  start the service (blocking, + tray icon)
//   --install                 enable auto-start at user login (HKCU Run)
//   --uninstall               disable auto-start
#include <windows.h>

#include <shellapi.h>
#include <shlwapi.h>

#include <cstdio>
#include <ctime>
#include <string>

#include "autostart.h"
#include "hardware.h"
#include "honor_core.h"
#include "pipe_server.h"
#include "stores.h"
#include "tray.h"
#include "trigger_engine.h"
#include "ui_coordinator.h"

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")

namespace {

svc::UiCoordinator* g_ui = nullptr;
svc::TrayIcon* g_tray = nullptr;
DWORD g_mainThreadId = 0;

std::string NarrowU8(const std::wstring& w) {
    (void)w;
    return "";
}

void Log(const std::string& msg) {
    try {
        std::wstring dir = svc::DataDirW();
        std::wstring path = dir + L"\\service.log";
        HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE)
            return;
        std::time_t t = std::time(nullptr);
        std::tm tmv{};
        localtime_s(&tmv, &t);
        char ts[32]{};
        snprintf(ts, sizeof(ts), "%04d-%02d-%02d %02d:%02d:%02d  ",
                 tmv.tm_year + 1900, tmv.tm_mon + 1, tmv.tm_mday, tmv.tm_hour,
                 tmv.tm_min, tmv.tm_sec);
        std::string line = std::string(ts) + msg + "\r\n";
        DWORD wrote = 0;
        WriteFile(h, line.data(), (DWORD)line.size(), &wrote, nullptr);
        CloseHandle(h);
    } catch (...) {
    }
}

void CrashLog(const std::string& msg) {
    try {
        std::wstring path = svc::DataDirW() + L"\\crash.log";
        HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE)
            return;
        std::time_t t = std::time(nullptr);
        std::tm tmv{};
        localtime_s(&tmv, &t);
        char ts[32]{};
        snprintf(ts, sizeof(ts), "%04d-%02d-%02d %02d:%02d:%02d  ",
                 tmv.tm_year + 1900, tmv.tm_mon + 1, tmv.tm_mday, tmv.tm_hour,
                 tmv.tm_min, tmv.tm_sec);
        std::string line = std::string(ts) + "tray menu: " + msg + "\r\n";
        DWORD wrote = 0;
        WriteFile(h, line.data(), (DWORD)line.size(), &wrote, nullptr);
        CloseHandle(h);
    } catch (...) {
    }
}

HICON LoadIconFromExe() {
    wchar_t path[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        HICON h = ExtractIconW(nullptr, path, 0);
        if (h && h != (HICON)1)
            return h;
    }
    HICON sys = LoadIconW(nullptr, IDI_APPLICATION);
    return CopyIcon(sys);
}

struct MenuJob {
    svc::UiCoordinator* ui;
    int cmd;
};

DWORD WINAPI MenuJobMain(LPVOID p) {
    MenuJob* j = (MenuJob*)p;
    // Handle off the pump thread: Quit() and perf-mode writes block on WMI,
    // and must not freeze the tray window.
    if (j->cmd == 1)
        j->ui->toggle();
    else if (j->cmd == 2) {
        j->ui->quit();
        PostThreadMessageW(g_mainThreadId, WM_QUIT, 0, 0);
    } else if (j->cmd == 11)
        svc::HardwareSetPerfMode(0);  // 智能
    else if (j->cmd == 12)
        svc::HardwareSetPerfMode(1);  // 高能
    else if (j->cmd == 13)
        svc::HardwareSetPerfMode(3);  // 狂战
    else if (j->cmd >= 20 && j->cmd <= 24)
        svc::HardwareSetPpm(j->cmd - 20);  // PPM 0..4 (狂战下有效)
    delete j;
    return 0;
}

void ShowTrayMenu() {
    try {
        if (!g_tray || !g_ui)
            return;
        int cur = svc::HardwareGetPerfMode();
        bool beast = cur == 3;
        int lastPpm = svc::HardwareLastPpm();
        std::vector<svc::TrayMenuItem> ppmItems;
        for (int lvl = 0; lvl <= 4; ++lvl) {
            wchar_t buf[64]{};
            swprintf_s(buf, L"PPM %d", lvl);
            svc::TrayMenuItem it;
            it.id = 20 + lvl;
            it.text = buf;
            it.checked = beast && lastPpm == lvl;
            it.disabled = !beast;  // PPM 只在狂战模式下可写
            ppmItems.push_back(std::move(it));
        }
        std::vector<svc::TrayMenuItem> items = {
            {11, L"智能模式", cur == 0, false, false, {}},
            {12, L"高能模式", cur == 1, false, false, {}},
            {13, L"狂战模式", cur == 3, false, false, {}},
            {0, L"PPM 功率级别", false, false, false, std::move(ppmItems)},
            {0, L"", false, true, false, {}},
            {1, L"显示 / 隐藏 UI", false, false, false, {}},
            {2, L"退出服务", false, false, false, {}},
        };
        POINT pt{};
        GetCursorPos(&pt);
        int chosen = g_tray->showMenu(pt.x, pt.y, items);
        if (chosen == 1 || chosen == 2 || chosen == 11 || chosen == 12 ||
            chosen == 13 || (chosen >= 20 && chosen <= 24)) {
            MenuJob* j = new MenuJob{g_ui, chosen};
            HANDLE th = CreateThread(nullptr, 0, &MenuJobMain, j, 0, nullptr);
            if (th)
                CloseHandle(th);
            else
                delete j;
        }
    } catch (...) {
        CrashLog("exception");
    }
}

}  // namespace

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, LPWSTR cmd, int) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::wstring arg = argc > 1 ? argv[1] : L"";
    if (argv)
        LocalFree(argv);
    (void)cmd;
    for (auto& ch : arg)
        if (ch >= L'A' && ch <= L'Z')
            ch = (wchar_t)(ch + 32);

    if (arg == L"--install") {
        wchar_t exe[MAX_PATH]{};
        DWORD n = GetModuleFileNameW(nullptr, exe, MAX_PATH);
        if (n > 0 && n < MAX_PATH)
            svc::AutoStartEnable(exe);
        Log(std::string("Auto-start at login ENABLED: ") +
            (svc::AutoStartIsEnabled() ? "on" : "off"));
        return 0;
    }
    if (arg == L"--uninstall") {
        svc::AutoStartDisable();
        Log(std::string("Auto-start at login DISABLED: ") +
            (svc::AutoStartIsEnabled() ? "on" : "off"));
        return 0;
    }

    // Single instance: a second copy must not start serving the same pipe —
    // two owners load-balance clients, so a SaveTriggers can land on A while
    // GetTriggers reads stale state from B (looks like "rules won't save").
    HANDLE singleMutex =
        CreateMutexW(nullptr, FALSE, L"Local\\HonorHelperServiceMutex");
    if (singleMutex != nullptr && GetLastError() == ERROR_ALREADY_EXISTS) {
        // Wake the existing instance's tray instead of silently doubling.
        svc::TrayIcon::NotifyExistingInstance();
        CloseHandle(singleMutex);
        return 0;
    }

    g_mainThreadId = GetCurrentThreadId();

    svc::HardwareInit();

    svc::SettingsStore settings;
    svc::TriggerStore triggers;

    svc::PipeServer server(settings, triggers);
    server.start();

    svc::TriggerEngine engine(triggers);
    engine.start();

    svc::UiCoordinator ui;
    g_ui = &ui;

    HICON hIcon = LoadIconFromExe();
    svc::TrayIcon tray(inst, hIcon, L"H-Helper — HONOR 控制中心");
    g_tray = &tray;
    tray.onLeft = [&] { ui.show(); };
    tray.onRight = &ShowTrayMenu;

    {
        char line[128]{};
        snprintf(line, sizeof(line), "honor-helper-service started. Admin=%d TempsEvery=%ds",
                 svc::HardwareIsAdmin() ? 1 : 0, settings.tempPollSeconds());
        Log(line);
    }

    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    engine.stop();
    server.stop();

    ui.quit();
    g_ui = nullptr;
    g_tray = nullptr;  // tray dtor removes the icon (declared after ui: LIFO)

    svc::HardwareShutdown();
    if (singleMutex != nullptr)
        CloseHandle(singleMutex);
    Log("honor-helper-service stopping.");
    return 0;
}
