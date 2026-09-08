#pragma once
// Shared wire types (mirrors common/Protocol) + the named-pipe framing helpers.
// Kept header-only for easy reuse by pipe_server.cpp and its unit checks.
//
// Framing on the wire (little-endian int32):
//   request: [kind][payloadLen][payload=UTF-8 JSON or raw bytes]
//   reply:   [kind][ok 0/1][errLen][errUtf8][payloadLen][payload=UTF-8 JSON]
#include <cstdint>
#include <string>
#include <vector>

#include "json.h"

namespace svc {

// ---- request ids (must match common/Protocol/Requests.cs) ----
enum class RequestType : int32_t {
    GetSnapshot = 0,
    ApplyProfile = 1,
    SetPpm = 2,
    SetTouchpad = 3,
    SetPerfMode = 4,
    RunGpuFix = 5,
    GetGpuStats = 6,
    GetOcInfo = 7,
    ApplyOcOffset = 8,
    GetSettings = 9,
    SetSettings = 10,
    GetTriggers = 11,
    SaveTriggers = 12,
    RunTriggerAction = 13,
    Ping = 14,
    SetChargeThreshold = 15,
};

inline const char* kPipeName = "honor-helper.svc";
inline const char* kPipePath = "\\\\.\\pipe\\honor-helper.svc";
inline const int kMaxMessage = 64 * 1024 * 1024;

// ---- wire DTOs (field names serialize as camelCase, matching WireJsonContext) ----

struct SimpleResult {
    bool ok = false;
    std::string message;
};

struct TempReading {
    std::string name;  // UTF-8
    int degreeC = 0;
};

struct FanReading {
    int id = 0;
    int rpm = 0;
    bool hasLimit = false;
    int limit = 0;
};

struct GpuStatsWire {
    int clockMhz = -1;
    int utilPct = -1;
};

struct GpuFixResult {
    int nvapiStatus = 0;
    int clockResetOk = 0;
    int smiFound = 0;
    std::string log;  // UTF-8
};

struct OcInfo {
    bool available = false;
    bool hasLastError = false;
    std::string lastError;  // UTF-8
};

struct StatusSnapshot {
    int mode = -1;
    std::vector<TempReading> temps;
    std::vector<FanReading> fans;
    bool hasTouchpad = false;
    int touchpad = 0;
    bool hasCpuFreq = false;
    int cpuFreqMhz = 0;
    bool hasGpu = false;
    GpuStatsWire gpu;
    bool isAdmin = false;
    // Last PPM level successfully written to EC (-1/分缺席 = unknown).
    // EC has no PPM readback command, so the service remembers the last
    // successful write (trigger, slider, or profile) and the UI mirrors it.
    bool hasLastPpm = false;
    int lastPpm = 0;
};

struct TriggerDtoWire {
    std::string path;  // UTF-8
    std::string openAction;
    int openPpm = 2;
    std::string closeAction;
    std::string openTouchpad;
    std::string closeTouchpad;
    int gpuCoreMhz = 0;
    int gpuMemMhz = 0;
    bool gpuFixOnExit = false;
    bool enabled = true;
};

// ---- JSON serializers (camelCase keys) ----

inline std::string ToWire(const SimpleResult& r) {
    return std::string("{\"ok\":") + (r.ok ? "true" : "false") +
           ",\"message\":" + QuoteJson(r.message) + "}";
}

inline std::string ToWire(const GpuStatsWire& g) {
    return "{\"clockMhz\":" + std::to_string(g.clockMhz) +
           ",\"utilPct\":" + std::to_string(g.utilPct) + "}";
}

inline std::string ToWire(const GpuFixResult& r) {
    return std::string("{\"nvapiStatus\":") + std::to_string(r.nvapiStatus) +
           ",\"clockResetOk\":" + std::to_string(r.clockResetOk) +
           ",\"smiFound\":" + std::to_string(r.smiFound) + ",\"log\":" +
           QuoteJson(r.log) + "}";
}

inline std::string ToWire(const OcInfo& o) {
    std::string e = o.hasLastError ? QuoteJson(o.lastError) : "null";
    return std::string("{\"available\":") + (o.available ? "true" : "false") +
           ",\"lastError\":" + e + "}";
}

inline std::string ToWire(const StatusSnapshot& s) {
    std::string o = "{\"mode\":" + std::to_string(s.mode) + ",\"temps\":[";
    for (size_t i = 0; i < s.temps.size(); ++i) {
        if (i)
            o.push_back(',');
        o += "{\"name\":" + QuoteJson(s.temps[i].name) +
             ",\"degreeC\":" + std::to_string(s.temps[i].degreeC) + "}";
    }
    o += "],\"fans\":[";
    for (size_t i = 0; i < s.fans.size(); ++i) {
        if (i)
            o.push_back(',');
        o += "{\"id\":" + std::to_string(s.fans[i].id) +
             ",\"rpm\":" + std::to_string(s.fans[i].rpm) + ",\"limit\":";
        o += s.fans[i].hasLimit ? std::to_string(s.fans[i].limit) : "null";
        o.push_back('}');
    }
    o += "],\"touchpad\":";
    o += s.hasTouchpad ? std::to_string(s.touchpad) : "null";
    o += ",\"cpuFreqMhz\":";
    o += s.hasCpuFreq ? std::to_string(s.cpuFreqMhz) : "null";
    o += ",\"gpu\":";
    o += s.hasGpu ? ToWire(s.gpu) : "null";
    o += ",\"isAdmin\":";
    o += s.isAdmin ? "true" : "false";
    o += ",\"lastPpm\":";
    o += s.hasLastPpm ? std::to_string(s.lastPpm) : "null";
    o.push_back('}');
    return o;
}

inline std::string ToWireTrigger(const TriggerDtoWire& t) {
    return std::string("{\"path\":") + QuoteJson(t.path) +
           ",\"openAction\":" + QuoteJson(t.openAction) +
           ",\"openPpm\":" + std::to_string(t.openPpm) +
           ",\"closeAction\":" + QuoteJson(t.closeAction) +
           ",\"openTouchpad\":" + QuoteJson(t.openTouchpad) +
           ",\"closeTouchpad\":" + QuoteJson(t.closeTouchpad) +
           ",\"gpuCoreMhz\":" + std::to_string(t.gpuCoreMhz) +
           ",\"gpuMemMhz\":" + std::to_string(t.gpuMemMhz) +
           ",\"gpuFixOnExit\":" + (t.gpuFixOnExit ? "true" : "false") +
           ",\"enabled\":" + (t.enabled ? "true" : "false") + "}";
}

// NOTE: the C# client deserializes GetTriggers as List<TriggerDto> (bare array,
// not wrapped in TriggerEditDto), matching PipeServer.Serialize(_triggers.AsDtos()).
inline std::string ToWireTriggers(const std::vector<TriggerDtoWire>& v) {
    std::string o = "[";
    for (size_t i = 0; i < v.size(); ++i) {
        if (i)
            o.push_back(',');
        o += ToWireTrigger(v[i]);
    }
    o.push_back(']');
    return o;
}

// ---- JSON request-body decoders (case-insensitive keys) ----

inline const JsonValue* F(const JsonValue& o, const char* a, const char* b) {
    const JsonValue* v = o.find(a);
    return v ? v : o.findI(b);
}

// ApplyProfileDto{profileId, ppm?}
inline void FromWireProfile(const JsonValue& o, std::string& id, bool& hasPpm, int& ppm) {
    const JsonValue* pid = F(o, "profileId", "ProfileId");
    id = pid ? pid->asString() : "";
    const JsonValue* p = F(o, "ppm", "Ppm");
    hasPpm = p && !p->isNull();
    ppm = p ? (int)p->asInt(0) : 0;
}

// OcOffsetDto{clockDomain, offsetMhz}
inline void FromWireOc(const JsonValue& o, int& domain, int& offset) {
    const JsonValue* d = F(o, "clockDomain", "ClockDomain");
    const JsonValue* m = F(o, "offsetMhz", "OffsetMhz");
    domain = d ? (int)d->asInt(0) : 0;
    offset = m ? (int)m->asInt(0) : 0;
}

// ChargeThresholdDto{lower, upper}
inline void FromWireChargeThreshold(const JsonValue& o, int& lower, int& upper) {
    const JsonValue* lo = F(o, "lower", "Lower");
    const JsonValue* hi = F(o, "upper", "Upper");
    lower = lo ? (int)lo->asInt(-1) : -1;
    upper = hi ? (int)hi->asInt(-1) : -1;
}

// SettingsDto{tempPollSeconds}
inline int FromWireSettings(const JsonValue& o) {
    const JsonValue* v = F(o, "tempPollSeconds", "TempPollSeconds");
    return v ? (int)v->asInt(5) : 5;
}

inline void FromWireTrigger(const JsonValue& o, TriggerDtoWire& t) {
    const JsonValue* v = nullptr;
    v = F(o, "path", "Path");
    t.path = v ? v->asString() : "";
    v = F(o, "openAction", "OpenAction");
    t.openAction = v ? v->asString("none") : "none";
    v = F(o, "openPpm", "OpenPpm");
    t.openPpm = v ? (int)v->asInt(2) : 2;
    v = F(o, "closeAction", "CloseAction");
    t.closeAction = v ? v->asString("none") : "none";
    v = F(o, "openTouchpad", "OpenTouchpad");
    t.openTouchpad = v ? v->asString("none") : "none";
    v = F(o, "closeTouchpad", "CloseTouchpad");
    t.closeTouchpad = v ? v->asString("none") : "none";
    v = F(o, "gpuCoreMhz", "GpuCoreMhz");
    t.gpuCoreMhz = v ? (int)v->asInt(0) : 0;
    v = F(o, "gpuMemMhz", "GpuMemMhz");
    t.gpuMemMhz = v ? (int)v->asInt(0) : 0;
    v = F(o, "gpuFixOnExit", "GpuFixOnExit");
    t.gpuFixOnExit = v ? v->asBool(false) : false;
    v = F(o, "enabled", "Enabled");
    // Missing Enabled defaults to true (old configs predate the field).
    t.enabled = v ? v->asBool(true) : true;
}

inline std::vector<TriggerDtoWire> FromWireTriggerList(const JsonValue& root) {
    std::vector<TriggerDtoWire> out;
    const JsonValue* arr = nullptr;
    if (root.type == JsonValue::Type::Arr) {
        arr = &root;
    } else if (root.type == JsonValue::Type::Obj) {
        // SaveTriggers posts TriggerEditDto{items:[...]}.
        arr = F(root, "items", "Items");
    }
    if (!arr || arr->type != JsonValue::Type::Arr)
        return out;
    for (const auto& el : arr->arr) {
        TriggerDtoWire t;
        if (el.type == JsonValue::Type::Obj)
            FromWireTrigger(el, t);
        out.push_back(std::move(t));
    }
    return out;
}

// ---- framing helpers ----

inline void AppendI32(std::vector<char>& buf, int32_t v) {
    buf.push_back((char)(v & 0xFF));
    buf.push_back((char)((v >> 8) & 0xFF));
    buf.push_back((char)((v >> 16) & 0xFF));
    buf.push_back((char)((v >> 24) & 0xFF));
}

inline int32_t ReadI32(const char* p) {
    const auto* u = (const unsigned char*)p;
    return (int32_t)(u[0] | (u[1] << 8) | (u[2] << 16) | (u[3] << 24));
}

// Encode: reply[ kind, ok, errLen, err, payloadLen, payload ].
inline std::vector<char> EncodeReply(int32_t kind, bool ok, const std::string& errUtf8,
                                     const std::string& payloadUtf8) {
    std::vector<char> buf;
    buf.reserve(16 + errUtf8.size() + payloadUtf8.size());
    AppendI32(buf, kind);
    AppendI32(buf, ok ? 1 : 0);
    AppendI32(buf, (int32_t)errUtf8.size());
    buf.insert(buf.end(), errUtf8.begin(), errUtf8.end());
    AppendI32(buf, (int32_t)payloadUtf8.size());
    buf.insert(buf.end(), payloadUtf8.begin(), payloadUtf8.end());
    return buf;
}

}  // namespace svc
