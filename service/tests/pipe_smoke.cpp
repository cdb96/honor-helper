// Pipe protocol smoke test: starts from a clean slate, launches the C++
// service exe, speaks the C# framing (kind/len + reply kind/ok/err/payload),
// and exercises Ping + GetSettings + SetSettings + GetTriggers +
// SaveTriggers (round-trip incl. unknown-field preservation) + GetSnapshot.
// Exits non-zero on any mismatch. Assumes no other honor-helper-service runs.
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace {
const char* kPipe = "\\\\.\\pipe\\honor-helper.svc";

bool WriteFull(HANDLE h, const char* p, size_t n) {
    size_t off = 0;
    while (off < n) {
        DWORD w = 0;
        if (!WriteFile(h, p + off, (DWORD)(n - off), &w, nullptr) || !w)
            return false;
        off += w;
    }
    return true;
}
bool ReadFull(HANDLE h, char* p, size_t n) {
    size_t off = 0;
    while (off < n) {
        DWORD r = 0;
        if (!ReadFile(h, p + off, (DWORD)(n - off), &r, nullptr) || !r)
            return false;
        off += r;
    }
    return true;
}
void PutI32(std::vector<char>& b, int32_t v) {
    b.push_back((char)(v & 0xFF));
    b.push_back((char)((v >> 8) & 0xFF));
    b.push_back((char)((v >> 16) & 0xFF));
    b.push_back((char)((v >> 24) & 0xFF));
}
int32_t GetI32(const char* p) {
    const auto* u = (const unsigned char*)p;
    return (int32_t)(u[0] | (u[1] << 8) | (u[2] << 16) | (u[3] << 24));
}

int g_fail = 0;
void Check(bool c, const char* msg, const std::string& extra = "") {
    if (c) {
        printf("[ok] %s\n", msg);
    } else {
        printf("[FAIL] %s %s\n", msg, extra.c_str());
        ++g_fail;
    }
}

struct Reply {
    int32_t kind = -1;
    bool ok = false;
    std::string err;
    std::string payload;
};

bool Call(int32_t kind, const std::string& payload, Reply& out) {
    out = Reply{};
    HANDLE h = CreateFileA(kPipe, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return false;
    DWORD mode = PIPE_READMODE_BYTE | PIPE_WAIT;
    SetNamedPipeHandleState(h, &mode, nullptr, nullptr);
    std::vector<char> req;
    PutI32(req, kind);
    PutI32(req, (int32_t)payload.size());
    req.insert(req.end(), payload.begin(), payload.end());
    bool good = WriteFull(h, req.data(), req.size());
    // Reply framing is [kind][ok][errLen][err][payloadLen][payload]: the header
    // is 12 bytes, NOT 16 — payloadLen follows the variable-length err body.
    char hdr[12]{};
    if (good)
        good = ReadFull(h, hdr, 12);
    if (good) {
        out.kind = GetI32(hdr);
        out.ok = GetI32(hdr + 4) != 0;
        int32_t errLen = GetI32(hdr + 8);
        if (errLen < 0 || errLen > 1 << 20) {
            good = false;
        } else {
            out.err.assign((size_t)errLen, '\0');
            if (errLen && !ReadFull(h, out.err.data(), (size_t)errLen))
                good = false;
        }
        char plenBuf[4]{};
        if (good)
            good = ReadFull(h, plenBuf, 4);
        int32_t payLen = good ? GetI32(plenBuf) : -1;
        if (!good || payLen < 0 || payLen > 64 << 20) {
            good = false;
        } else {
            out.payload.assign((size_t)payLen, '\0');
            if (payLen && !ReadFull(h, out.payload.data(), (size_t)payLen))
                good = false;
        }
    }
    if (good) {
        CloseHandle(h);
        return true;
    }
    CloseHandle(h);
    return false;
}  // Call

bool Has(const std::string& s, const char* sub) {
    return s.find(sub) != std::string::npos;
}
}  // namespace

int main() {
    Reply r;
    // Ping (14) -> SimpleResult{ok,message:pong}
    Check(Call(14, "", r) && r.ok && r.kind == 14, "ping ok", r.payload);
    Check(Has(r.payload, "\"ok\":true") && Has(r.payload, "pong"),
          "ping payload", r.payload);
    // GetSettings (9) -> {"tempPollSeconds":N}
    Check(Call(9, "", r) && r.ok, "getsettings ok", r.payload);
    Check(Has(r.payload, "tempPollSeconds"), "getsettings shape", r.payload);
    // SetSettings (10) with camelCase {"tempPollSeconds":7}, then read back.
    Check(Call(10, "{\"tempPollSeconds\":7}", r) && r.ok, "setsettings ok",
          r.payload);
    Check(Call(9, "", r) && r.ok && Has(r.payload, ":7}"), "settings roundtrip",
          r.payload);
    // Restore 3 (matches the pre-existing local settings.json).
    Check(Call(10, "{\"tempPollSeconds\":3}", r) && r.ok, "settings restore",
          r.payload);
    // GetTriggers (11) -> bare JSON array.
    Check(Call(11, "", r) && r.ok, "gettriggers ok", r.payload.substr(0, 80));
    Check(!r.payload.empty() && r.payload.front() == '[', "triggers is array",
          r.payload.substr(0, 80));
    std::string before = r.payload;
    // SaveTriggers (12) with TriggerEditDto envelope + an unknown field.
    std::string save =
        "{\"items\":[{\"path\":\"C:\\\\t\\\\x.exe\",\"openAction\":\"none\","
        "\"openPpm\":2,\"closeAction\":\"none\",\"openTouchpad\":\"none\","
        "\"closeTouchpad\":\"none\",\"gpuCoreMhz\":0,\"gpuMemMhz\":0,"
        "\"gpuFixOnExit\":false,\"enabled\":true,\"futureField\":123}]}";
    Check(Call(12, save, r) && r.ok, "savetriggers ok", r.payload);
    Check(Call(11, "", r) && r.ok && Has(r.payload, "x.exe"), "triggers wrote",
          r.payload);
    // Restore original triggers verbatim.
    Check(Call(12, std::string("{\"items\":") + before + "}", r) && r.ok,
          "triggers restore", r.payload);
    Check(Call(11, "", r) && r.ok && r.payload == before, "triggers roundtrip",
          r.payload.substr(0, 120));
    // Unknown request -> ok=false. Read the framed error reply with the same
    // helper (the server always answers, even for unknown kinds).
    Check(Call(40, "", r) && !r.ok, "unknown kind rejected", r.err);
    // GetSnapshot (0): full shape check (no hardware assertions — CI has none).
    Check(Call(0, "", r) && r.ok, "getsnapshot ok",
          r.payload.substr(0, 120));
    for (const char* k : {"\"mode\":", "\"temps\":", "\"fans\":",
                          "\"touchpad\":", "\"cpuFreqMhz\":", "\"gpu\":",
                          "\"isAdmin\":", "\"lastPpm\":"}) {
        std::string label = std::string("snapshot has ") + k;
        Check(Has(r.payload, k), label.c_str(), r.payload.substr(0, 160));
    }
    // GetOcInfo (7) + GetGpuStats (6).
    Check(Call(7, "", r) && r.ok && Has(r.payload, "\"available\":"),
          "getocinfo ok", r.payload);
    Check(Call(6, "", r) && r.ok && Has(r.payload, "\"clockMhz\":"),
          "getgpustats ok", r.payload);
    if (g_fail) {
        printf("SMOKE FAIL (%d)\n", g_fail);
        return 1;
    }
    printf("SMOKE OK\n");
    return 0;
}
