// HardwareApi port: serializes all hardware access through one mutex and
// mirrors every message/behavior of the old C# PpmController + HardwareApi.
#include "hardware.h"

#include <atomic>

#include "honor_core.h"

namespace svc {

bool ProfileMode(const std::string& id, int& modeOut) {
    if (id == "smart") {
        modeOut = 0;
        return true;
    }
    if (id == "high") {
        modeOut = 1;
        return true;
    }
    if (id == "beast") {
        modeOut = 3;
        return true;
    }
    return false;
}

std::string ProfileDisplay(const std::string& id) {
    if (id == "smart")
        return "智能  (Smart / Balanced)";
    if (id == "high")
        return "高能  (High Energy)";
    if (id == "beast")
        return "狂战  (Rage / Beast)";
    return id;
}

void HardwareInit() { hc_init(); }
void HardwareShutdown() { hc_shutdown(); }
bool HardwareIsAdmin() { return hc_is_admin() != 0; }
int HardwareGetPerfMode() { return hc_get_perf_mode(); }
bool HardwareSetPerfMode(int mode) { return hc_set_perf_mode(mode) != 0; }

namespace {
// Last PPM level confirmed written (EC offers no readback). Guarded by the
// WMI path's own serialization: every writer runs under hc's internal lock
// long enough that a plain atomic is sufficient for the UI mirror.
std::atomic<int> g_lastPpm{-1};
}  // namespace

bool HardwareSetPpm(int level) {
    bool ok = hc_set_ppm(level) != 0;
    if (ok && level >= 0 && level <= 4)
        g_lastPpm.store(level, std::memory_order_relaxed);
    return ok;
}

int HardwareLastPpm() { return g_lastPpm.load(std::memory_order_relaxed); }

bool HardwareGetTemp(int channel, int& outC) {
    int t = hc_get_temp(channel);
    if (t < 0)
        return false;
    outC = t;
    return true;
}

bool HardwareGetFan(int id, int& rpmOut, bool& hasLimit, int& limitOut) {
    int32_t limit = -1;
    int rpm = hc_get_fan_speed(id, &limit);
    if (rpm < 0)
        return false;
    rpmOut = rpm;
    hasLimit = limit >= 0;
    limitOut = limit;
    return true;
}

bool HardwareGetTouchpad(int& out) {
    int s = hc_get_touchpad_state();
    if (s < 0)
        return false;
    out = s;
    return true;
}

bool HardwareSetTouchpad(bool on) { return hc_set_touchpad_state(on ? 1 : 0) != 0; }

bool HardwareGetCpuFreq(int& outMhz) {
    int f = hc_get_cpu_freq_mhz();
    if (f < 0)
        return false;
    outMhz = f;
    return true;
}

bool HardwareGetGpuStats(GpuStatsWire& out) {
    hc_gpu_stats s{};
    if (hc_gpu_stats_query(&s) == 0)
        return false;
    out.clockMhz = s.clock_mhz;
    out.utilPct = s.util_pct;
    return true;
}

int HardwareOcApply(int clockDomain, int offsetMhz) {
    return hc_oc_apply_offset(clockDomain, offsetMhz);
}

bool HardwareOcAvailable(std::string& errUtf8Out) {
    int ok = hc_oc_available();
    const char* e = hc_oc_last_error();
    errUtf8Out = (e && *e) ? e : "";
    return ok != 0;
}

void HardwareGpuFix(bool skipClockReset, GpuFixResult& out) {
    hc_gpu_fix fix{};
    int ok = hc_gpu_clockfix_run(skipClockReset ? 1 : 0, &fix);
    const char* log = hc_last_log();
    std::string msg = (log && *log) ? log : "";
    if (fix.clock_reset_ok != 0) {
        if (!msg.empty() && msg.back() != '\n')
            msg.push_back('\n');
        msg += "-rgc: 已执行（无输出）\n";
    }
    while (!msg.empty() && (msg.back() == '\n' || msg.back() == '\r' ||
                             msg.back() == ' ' || msg.back() == '\t'))
        msg.pop_back();
    out.nvapiStatus = fix.nvapi_status;
    out.clockResetOk = fix.clock_reset_ok;
    out.smiFound = fix.smi_found;
    out.log = std::move(msg);
    (void)ok;
}

StatusSnapshot HardwareGetSnapshot() {
    StatusSnapshot s;
    s.mode = HardwareGetPerfMode();
    for (const auto& c : kTempChannels) {
        int t = 0;
        if (HardwareGetTemp(c.channel, t) && t >= 0) {
            TempReading r;
            r.name = c.name;
            r.degreeC = t;
            s.temps.push_back(std::move(r));
        }
    }
    for (const auto& f : kFans) {
        int rpm = 0, limit = 0;
        bool has = false;
        if (HardwareGetFan(f.id, rpm, has, limit)) {
            FanReading r;
            r.id = f.id;
            r.rpm = rpm;
            r.hasLimit = has;
            r.limit = limit;
            s.fans.push_back(std::move(r));
        }
    }
    int tp = 0;
    if (HardwareGetTouchpad(tp)) {
        s.hasTouchpad = true;
        s.touchpad = tp;
    }
    int cpu = 0;
    if (HardwareGetCpuFreq(cpu)) {
        s.hasCpuFreq = true;
        s.cpuFreqMhz = cpu;
    }
    GpuStatsWire g{};
    if (HardwareGetGpuStats(g)) {
        s.hasGpu = true;
        s.gpu = g;
    }
    s.isAdmin = HardwareIsAdmin();
    int lastPpm = HardwareLastPpm();
    if (lastPpm >= 0 && lastPpm <= 4) {
        s.hasLastPpm = true;
        s.lastPpm = lastPpm;
    }
    return s;
}

SimpleResult HardwareApplyProfile(const std::string& profileId, bool hasPpm, int ppm) {
    int mode = 0;
    if (!ProfileMode(profileId, mode))
        return {false, "未知配置：" + profileId};
    std::string display = ProfileDisplay(profileId);
    if (HardwareSetPerfMode(mode)) {
        std::string msg = "已切换：" + display +
                          "（perf " + std::to_string(mode) + "）";
        if (hasPpm && mode == kBeastPerfMode) {
            if (HardwareSetPpm(ppm))
                msg += " · PPM = " + std::to_string(ppm);
            else
                msg += " · PPM 设置失败";
        }
        return {true, std::move(msg)};
    }
    if (mode != kBeastPerfMode && HardwareSetPerfMode(kBeastPerfMode)) {
        std::string msg = "已切换：狂战 (Beast)"
                          "（目标 " +
                          display + " 不可用）";
        if (hasPpm && HardwareSetPpm(ppm))
            msg += " · PPM = " + std::to_string(ppm);
        return {true, std::move(msg)};
    }
    return {false, "切换失败：无法写入 HONOR WMI。请以管理员身份运行。"};
}

}  // namespace svc
