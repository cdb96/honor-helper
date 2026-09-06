// AutoStart port: same key, same value name, same quoted "--background" value.
#include "autostart.h"

#include <windows.h>

#include <string>

namespace svc {

namespace {
const wchar_t* kRunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const wchar_t* kValue = L"HonorHelperService";
}  // namespace

void AutoStartEnable(const wchar_t* exePath) {
    if (!exePath || !*exePath)
        return;
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kRunKey, 0, nullptr, 0,
                        KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        return;
    std::wstring v = L"\"";
    v += exePath;
    v += L"\" --background";
    RegSetValueExW(key, kValue, 0, REG_SZ, (const BYTE*)v.c_str(),
                   (DWORD)((v.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
}

void AutoStartDisable() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_SET_VALUE, &key) !=
        ERROR_SUCCESS)
        return;
    RegDeleteValueW(key, kValue);
    RegCloseKey(key);
}

bool AutoStartIsEnabled() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_QUERY_VALUE, &key) !=
        ERROR_SUCCESS)
        return false;
    DWORD type = 0, size = 0;
    LONG rc = RegQueryValueExW(key, kValue, nullptr, &type, nullptr, &size);
    RegCloseKey(key);
    return rc == ERROR_SUCCESS && size > 0;
}

}  // namespace svc
