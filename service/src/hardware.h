#pragma once
// Thin C++ fa莽ade over honor_core (same TU as the service exe, no DLL).
// Reads:  perf mode, temps, fans, touchpad, cpu freq, gpu stats/oc info.
// Writes: perf mode / ppm / touchpad / profile(fan curve if honored) / OC.
#include <cstdint>
#include <string>
#include <vector>

#include "protocol.h"  // GpuFixResult, OcInfo, StatusSnapshot, GpuStatsWire

namespace svc {

struct TempChannel {
    int channel = 0;
    const char* name = "";  // UTF-8
};

struct FanChannel {
    int id = 0;
    const char* name = "";  // UTF-8 (unused on the wire, kept for parity)
};

// Must stay identical to common/CoreTables.cs (one entry per line; no
// trailing comments so no entry can be swallowed by a "//").
inline const TempChannel kTempChannels[] = {
    {0x00, "CPU"},
    {0x01, "GPU"},
    {0x16, "风扇"},
    {0x0B, "内存"},
    {0x2B, "EC"},
    {0x05, "芯片组"},
    {0x08, "充电区"},
    {0x0E, "电池"},
    {0x15, "DC口"},
    {0x0F, "主板"},
    {0x2D, "CPU供电"},
    {0x2C, "GPU供电"},
};

inline const FanChannel kFans[] = {
    {0, "CPU"},
    {1, "GPU"},
    {2, "系统"},
};

inline const int kBeastPerfMode = 3;

// profile id -> perf mode ("smart"=0, "high"=1, "beast"=3).
bool ProfileMode(const std::string& profileId, int& modeOut);
// Display name used in ApplyProfile success messages.
std::string ProfileDisplay(const std::string& profileId);

void HardwareInit();
void HardwareShutdown();
bool HardwareIsAdmin();
int HardwareGetPerfMode();  // -1 on failure
bool HardwareSetPerfMode(int mode);
bool HardwareSetPpm(int level);
bool HardwareGetTemp(int channel, int& outC);
bool HardwareGetFan(int id, int& rpmOut, bool& hasLimit, int& limitOut);
bool HardwareGetTouchpad(int& out);  // out: 1 on / 0 off
bool HardwareSetTouchpad(bool on);
bool HardwareGetCpuFreq(int& outMhz);
bool HardwareGetGpuStats(GpuStatsWire& out);
int HardwareOcApply(int clockDomain, int offsetMhz);  // NVAPI rc (0 ok)
bool HardwareOcAvailable(std::string& errUtf8Out);
void HardwareGpuFix(bool skipClockReset, GpuFixResult& out);

// Full snapshot, mirroring HardwareApi.GetSnapshot() field-by-field.
StatusSnapshot HardwareGetSnapshot();
// Last successful PPM write (EC has no readback; -1 = unknown).
int HardwareLastPpm();
// Mirrors PpmController.ApplyProfile(): perf mode + optional PPM on beast,
// with the "target unavailable -> fall back to beast" path.
SimpleResult HardwareApplyProfile(const std::string& profileId, bool hasPpm, int ppm);

}  // namespace svc
