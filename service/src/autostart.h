#pragma once
// HKCU\...\Run auto-start ("HonorHelperService" value).
namespace svc {
void AutoStartEnable(const wchar_t* exePath);  // writes "<exe>" --background
void AutoStartDisable();
bool AutoStartIsEnabled();
}  // namespace svc
