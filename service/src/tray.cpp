// TrayIcon port: same class name, same GUID-per-run window class, same
// per-run tray callback message, same fixed "HonorHelperActivate" message,
// same re-entrancy guard around TrackPopupMenu.
#include "tray.h"

#include <windows.h>

#include <objbase.h>
#include <shellapi.h>

#include <cstdio>

namespace svc {

namespace {
const UINT kTrayId = 1;
const UINT kWmAppTray = WM_APP;  // 0x8000 base used by the C# build (WM_APP_TRAY)
}  // namespace

TrayIcon::TrayIcon(HINSTANCE inst, HICON icon, const std::wstring& tooltip)
    : inst_(inst), icon_(icon), tip_(tooltip) {
    // GUID-style unique class name (matches "HonorHelperTray_" + Guid.NewGuid()).
    GUID g{};
    CoCreateGuid(&g);
    wchar_t gs[64]{};
    swprintf_s(gs, L"%08X%04X%04X%02X%02X%02X%02X%02X%02X%02X%02X", g.Data1,
               g.Data2, g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3],
               g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    cls_ = L"HonorHelperTray_";
    cls_ += gs;
    std::wstring msgName = L"HonorHelperTrayMsg_";
    msgName += gs;
    wmTray_ = RegisterWindowMessageW(msgName.c_str());
    wmActivate_ = RegisterWindowMessageW(kActivateMsgName);

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = &TrayIcon::WndProc;
    wc.hInstance = inst_;
    wc.lpszClassName = cls_.c_str();
    RegisterClassExW(&wc);

    hwnd_ = CreateWindowExW(0, cls_.c_str(), L"", 0, 0, 0, 0, 0, nullptr,
                            nullptr, inst_, this);

    NOTIFYICONDATAW d{};
    d.cbSize = sizeof(d);
    d.hWnd = hwnd_;
    d.uID = kTrayId;
    d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    d.uCallbackMessage = wmTray_;
    d.hIcon = icon_;
    wcsncpy_s(d.szTip, tip_.c_str(), _TRUNCATE);
    if (Shell_NotifyIconW(NIM_ADD, &d))
        added_ = true;
}

TrayIcon::~TrayIcon() {
    onLeft = nullptr;
    onRight = nullptr;
    if (added_ && hwnd_) {
        NOTIFYICONDATAW d{};
        d.cbSize = sizeof(d);
        d.hWnd = hwnd_;
        d.uID = kTrayId;
        d.uFlags = NIF_MESSAGE;
        Shell_NotifyIconW(NIM_DELETE, &d);
        added_ = false;
    }
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    if (icon_) {
        DestroyIcon(icon_);
        icon_ = nullptr;
    }
    if (!cls_.empty())
        UnregisterClassW(cls_.c_str(), inst_);
}

void TrayIcon::setTooltip(const std::wstring& text) {
    if (!added_)
        return;
    NOTIFYICONDATAW d{};
    d.cbSize = sizeof(d);
    d.hWnd = hwnd_;
    d.uID = kTrayId;
    d.uFlags = NIF_TIP;
    wcsncpy_s(d.szTip, text.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &d);
}

namespace {
void AppendItems(HMENU menu, const std::vector<TrayMenuItem>& items,
                 std::vector<HMENU>& owned) {
    for (const auto& it : items) {
        if (it.separator) {
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            continue;
        }
        if (!it.sub.empty()) {
            HMENU sub = CreatePopupMenu();
            if (!sub)
                continue;
            owned.push_back(sub);
            AppendItems(sub, it.sub, owned);
            // A popup with no enabled/checked rows looks empty: keep it, but
            // gray the parent when every child is disabled.
            bool anyOn = false;
            for (const auto& c : it.sub)
                if (!c.separator && !c.disabled)
                    anyOn = true;
            AppendMenuW(menu, MF_POPUP | (anyOn ? 0 : MF_GRAYED),
                        (UINT_PTR)sub, it.text.c_str());
            continue;
        }
        AppendMenuW(menu,
                    MF_STRING | (it.checked ? MF_CHECKED : MF_UNCHECKED) |
                        (it.disabled ? MF_GRAYED : 0),
                    (UINT_PTR)it.id, it.text.c_str());
    }
}
}  // namespace

int TrayIcon::showMenu(int x, int y, const std::vector<TrayMenuItem>& items) {
    HMENU menu = CreatePopupMenu();
    if (!menu)
        return 0;
    menuOpen_ = true;
    std::vector<HMENU> owned;
    AppendItems(menu, items, owned);
    SetForegroundWindow(hwnd_);
    int chosen = (int)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x,
                                     y, 0, hwnd_, nullptr);
    // Swallow the WM_COMMAND TrackPopupMenu posts so they don't linger.
    MSG msg{};
    while (PeekMessageW(&msg, hwnd_, 0, 0, PM_REMOVE)) {
    }
    DestroyMenu(menu);
    for (HMENU s : owned)
        DestroyMenu(s);
    menuOpen_ = false;
    return chosen;
}

void TrayIcon::NotifyExistingInstance() {
    PostMessageW(HWND_BROADCAST, RegisterWindowMessageW(kActivateMsgName), 0, 0);
}

LRESULT CALLBACK TrayIcon::WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    TrayIcon* self = nullptr;
    if (m == WM_NCCREATE) {
        auto* cs = (CREATESTRUCTW*)l;
        self = (TrayIcon*)cs->lpCreateParams;
        SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)self);
        if (self)
            self->hwnd_ = h;
    } else {
        self = (TrayIcon*)(LONG_PTR)GetWindowLongPtrW(h, GWLP_USERDATA);
    }
    if (self) {
        if (m == self->wmTray_ && (UINT)w == kTrayId) {
            int ev = LOWORD((DWORD)(LONG_PTR)l);
            if (ev == WM_LBUTTONUP) {
                self->onTray(kTrayId, ev);
                return 0;
            }
            if (ev == WM_RBUTTONUP) {
                self->onTray(kTrayId, ev);
                return 0;
            }
        }
        if (m == self->wmActivate_)
            return 0;  // service owns the tray; nothing to activate here
    }
    return DefWindowProcW(h, m, w, l);
}

void TrayIcon::onTray(UINT id, LONG ev) {
    (void)id;
    if (menuOpen_)
        return;  // suppress re-entrant callbacks while a menu is open
    try {
        if (ev == WM_LBUTTONUP && onLeft)
            onLeft();
        else if (ev == WM_RBUTTONUP && onRight)
            onRight();
    } catch (...) {
    }
}

}  // namespace svc
