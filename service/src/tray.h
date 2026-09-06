#pragma once
// Win32 tray icon (Shell_NotifyIconW) with a hidden owner window.
// Left click -> Show() callback, right click -> context menu callback.
#include <windows.h>

#include <functional>
#include <string>
#include <vector>

namespace svc {

// One row of the tray context menu. checked -> MF_CHECKED tick;
// separator -> MF_SEPARATOR line (other fields ignored);
// disabled -> MF_GRAYED (also grays a popup parent);
// sub non-empty -> MF_POPUP submenu (id ignored).
struct TrayMenuItem {
    int id = 0;
    std::wstring text;
    bool checked = false;
    bool separator = false;
    bool disabled = false;
    std::vector<TrayMenuItem> sub;
};

class TrayIcon {
public:
    TrayIcon(HINSTANCE inst, HICON icon, const std::wstring& tooltip);
    ~TrayIcon();

    TrayIcon(const TrayIcon&) = delete;
    TrayIcon& operator=(const TrayIcon&) = delete;

    bool added() const { return added_; }
    HWND hwnd() const { return hwnd_; }

    std::function<void()> onLeft;
    std::function<void()> onRight;

    void setTooltip(const std::wstring& text);

    // Show a native popup menu at (x,y); returns the chosen id or 0 if dismissed.
    // Must run on the message-pump (owner) thread.
    int showMenu(int x, int y, const std::vector<TrayMenuItem>& items);

    // Second-instance helper: broadcast the fixed activate message.
    static void NotifyExistingInstance();

private:
    HINSTANCE inst_ = nullptr;
    HICON icon_ = nullptr;
    std::wstring tip_;
    std::wstring cls_;
    HWND hwnd_ = nullptr;
    UINT wmTray_ = 0;
    UINT wmActivate_ = 0;
    bool added_ = false;
    bool menuOpen_ = false;  // re-entrancy guard (nested TrackPopupMenu loop)

    static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l);
    void onTray(UINT id, LONG ev);
};

inline const wchar_t* kActivateMsgName = L"HonorHelperActivate";

}  // namespace svc
