#pragma once
// Coordinates the UI process from the service side: launch on demand,
// broadcast show/hide/quit window-messages, track the running child.
#include <windows.h>

#include <string>

namespace svc {

class UiCoordinator {
public:
    UiCoordinator();
    ~UiCoordinator();

    UiCoordinator(const UiCoordinator&) = delete;
    UiCoordinator& operator=(const UiCoordinator&) = delete;

    bool isRunning() const;
    void show();    // activate if running, else launch
    void hide();    // broadcast Hide (UI closes => process exits)
    void toggle();  // hide if running, else show
    void quit();    // broadcast Quit, wait 2s, kill if needed

private:
    HANDLE child_ = nullptr;  // process handle (null when not launched-by-us)
    DWORD childPid_ = 0;
    UINT msgShow_ = 0;
    UINT msgHide_ = 0;
    UINT msgQuit_ = 0;
    std::wstring uiExe_;  // honor-helper.exe next to the service exe

    void broadcast(UINT msg) const;
    bool pollRunning() const;  // refresh child_ by pid (handles external exit)
};

}  // namespace svc
