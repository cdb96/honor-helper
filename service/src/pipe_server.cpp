// PipeServer: mirrors service/PipeServer.cs 鈥?same pipe name, same SDDL/medium
// integrity label, same framing, same request dispatch and reply JSON shapes.
#include "pipe_server.h"

#include <windows.h>

#include <sddl.h>

#include <cstdio>

#include "hardware.h"
#include "json.h"
#include "protocol.h"
#include "stores.h"

#pragma comment(lib, "advapi32.lib")

namespace svc {

PipeServer::PipeServer(SettingsStore& settings, TriggerStore& triggers)
    : settings_(settings), triggers_(triggers) {}

PipeServer::~PipeServer() { stop(); }

void PipeServer::start() {
    stop_.store(false);
    if (readyEvent_ == nullptr)
        readyEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    else
        ResetEvent(readyEvent_);
    if (listenerThread_ == nullptr) {
        listenerThread_ =
            CreateThread(nullptr, 0, &PipeServer::ListenerMain, this, 0, nullptr);
    }
}

void PipeServer::stop() {
    stop_.store(true);
    // Break the blocked ConnectNamedPipe by connecting once ourselves.
    HANDLE h = CreateFileA(kPipePath, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        // Best-effort: the listener may already be gone.
        DWORD mode = PIPE_READMODE_BYTE | PIPE_WAIT;
        SetNamedPipeHandleState(h, &mode, nullptr, nullptr);
        CancelIo(h);
        CloseHandle(h);
    }
    if (listenerThread_ != nullptr) {
        DWORD rc = WaitForSingleObject(listenerThread_, 3000);
        if (rc == WAIT_TIMEOUT)
            TerminateThread(listenerThread_, 0);
        CloseHandle(listenerThread_);
        listenerThread_ = nullptr;
    }
    if (readyEvent_ != nullptr) {
        CloseHandle(readyEvent_);
        readyEvent_ = nullptr;
    }
}

DWORD WINAPI PipeServer::ListenerMain(LPVOID self) {
    ((PipeServer*)self)->listenerLoop();
    return 0;
}

static PSECURITY_DESCRIPTOR BuildMediumIntegrityDescriptor() {
    // Same SDDL as the C# NativePipeServer:
    // DACL allow BA/SY/AU/WD + medium mandatory label.
    static const wchar_t* kSddl =
        L"D:P(A;;GA;;;BA)(A;;GA;;;SY)(A;;GA;;;AU)(A;;GA;;;WD)"
        L"S:(ML;;NW;;;S-1-16-8192)";
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            kSddl, SDDL_REVISION_1, &sd, nullptr))
        return nullptr;
    return sd;
}

bool PipeServer::ReadFull(HANDLE h, char* buf, size_t n) {
    size_t off = 0;
    while (off < n) {
        DWORD chunk = (DWORD)((n - off > 1 << 20) ? 1 << 20 : n - off);
        DWORD got = 0;
        if (!ReadFile(h, buf + off, chunk, &got, nullptr) || got == 0)
            return false;
        off += got;
    }
    return true;
}

bool PipeServer::WriteFull(HANDLE h, const char* buf, size_t n) {
    size_t off = 0;
    while (off < n) {
        DWORD chunk = (DWORD)((n - off > 1 << 20) ? 1 << 20 : n - off);
        DWORD wrote = 0;
        if (!WriteFile(h, buf + off, chunk, &wrote, nullptr) || wrote == 0)
            return false;
        off += wrote;
    }
    return true;
}

void PipeServer::listenerLoop() {
    std::wstring wpath;
    {
        int n = MultiByteToWideChar(CP_UTF8, 0, kPipePath, -1, nullptr, 0);
        wpath.assign((size_t)(n > 0 ? n : 0), L'\0');
        if (n > 0)
            MultiByteToWideChar(CP_UTF8, 0, kPipePath, -1, wpath.data(), n);
        if (!wpath.empty() && wpath.back() == L'\0')
            wpath.pop_back();
    }
    PSECURITY_DESCRIPTOR sd = BuildMediumIntegrityDescriptor();
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = sd;
    sa.bInheritHandle = FALSE;

    while (!stop_.load()) {
        HANDLE pipe = CreateNamedPipeW(
            wpath.c_str(), PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES,
            8192, 8192, 0, sd ? &sa : nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            if (stop_.load())
                break;
            Sleep(250);
            continue;
        }
        BOOL connected = ConnectNamedPipe(pipe, nullptr);
        DWORD err = GetLastError();
        if (!connected && err != ERROR_PIPE_CONNECTED) {
            // ERROR_NO_DATA (232) = client connected then gave up before we
            // accepted (e.g. probe connects). Drop the instance, keep serving.
            CloseHandle(pipe);
            if (stop_.load())
                break;
            Sleep(50);
            continue;
        }
        if (stop_.load()) {
            // Swallow the stop poke on this fresh instance: do a best-effort
            // drain so the client connect is released, then close.
            DWORD mode = PIPE_READMODE_BYTE | PIPE_WAIT;
            SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr);
            char drain[8]{};
            DWORD got = 0;
            ReadFile(pipe, drain, sizeof(drain), &got, nullptr);
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
            break;
        }
        handleOne(pipe);
        // handleOne owns the handle lifetime.
    }
    if (sd)
        LocalFree(sd);
}

struct ConnArgs {
    PipeServer* self;
    HANDLE pipe;
};

void PipeServer::handleOne(HANDLE pipe) {
    // Serve concurrently: hand the connected handle to a worker thread.
    struct Ctx {
        PipeServer* self;
        HANDLE pipe;
    };
    Ctx* ctx = new Ctx{this, pipe};
    HANDLE th = CreateThread(
        nullptr, 0,
        [](LPVOID p) -> DWORD {
            Ctx* c = (Ctx*)p;
            HANDLE pipe = c->pipe;
            PipeServer* self = c->self;
            delete c;
            // One request -> one reply -> close (matches C# one-shot model).
            // Read the 8-byte header first; an immediate disconnect (probe /
            // stop-poke) just ends this worker without a reply.
            char hdr[8]{};
            DWORD got0 = 0;
            if (!ReadFile(pipe, hdr, 8, &got0, nullptr) || got0 == 0) {
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
                return 0;
            }
            size_t have = (size_t)got0;
            while (have < 8) {
                DWORD more = 0;
                if (!ReadFile(pipe, hdr + have, (DWORD)(8 - have), &more, nullptr) ||
                    more == 0) {
                    DisconnectNamedPipe(pipe);
                    CloseHandle(pipe);
                    return 0;
                }
                have += more;
            }
            int32_t kind = ReadI32(hdr);
            int32_t len = ReadI32(hdr + 4);
            if (len < 0 || len > kMaxMessage) {
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
                return 0;
            }
            std::vector<char> payload((size_t)len);
            if (len > 0 && !PipeServer::ReadFull(pipe, payload.data(), (size_t)len)) {
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
                return 0;
            }
            std::vector<char> reply =
                self->dispatch(kind, payload.data(), payload.size());
            PipeServer::WriteFull(pipe, reply.data(), reply.size());
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
            return 0;
        },
        ctx, 0, nullptr);
    if (th == nullptr) {
        delete ctx;
        CloseHandle(pipe);
    } else {
        CloseHandle(th);
    }
}

std::vector<char> PipeServer::dispatch(int32_t kind, const char* payload, size_t len) {
    auto ok = [&](const std::string& json) {
        return EncodeReply(kind, true, "", json);
    };
    auto fail = [&](const std::string& msg) {
        return EncodeReply(kind, false, msg, "");
    };
    RequestType req = (RequestType)kind;
    // Reject out-of-range kinds explicitly: a switch on an unlisted enum value
    // is fine, but be explicit so future RequestType growth can't slip through.
    int32_t raw = kind;
    if (raw != (int32_t)RequestType::GetSnapshot &&
        raw != (int32_t)RequestType::ApplyProfile &&
        raw != (int32_t)RequestType::SetPpm &&
        raw != (int32_t)RequestType::SetTouchpad &&
        raw != (int32_t)RequestType::SetPerfMode &&
        raw != (int32_t)RequestType::RunGpuFix &&
        raw != (int32_t)RequestType::GetGpuStats &&
        raw != (int32_t)RequestType::GetOcInfo &&
        raw != (int32_t)RequestType::ApplyOcOffset &&
        raw != (int32_t)RequestType::GetSettings &&
        raw != (int32_t)RequestType::SetSettings &&
        raw != (int32_t)RequestType::GetTriggers &&
        raw != (int32_t)RequestType::SaveTriggers &&
        raw != (int32_t)RequestType::RunTriggerAction &&
        raw != (int32_t)RequestType::Ping) {
        return EncodeReply(kind, false,
                           "未知请求", "");
    }
    try {
        switch (req) {
            case RequestType::Ping:
                return ok(ToWire(SimpleResult{true, "pong"}));
            case RequestType::GetSnapshot:
                return ok(ToWire(HardwareGetSnapshot()));
            case RequestType::ApplyProfile: {
                JsonValue root;
                if (len > 0 && !ParseJson(payload, len, root, nullptr))
                    return fail("bad request");
                std::string id;
                bool hasPpm = false;
                int ppm = 0;
                FromWireProfile(root, id, hasPpm, ppm);
                return ok(ToWire(HardwareApplyProfile(id, hasPpm, ppm)));
            }
            case RequestType::SetPpm: {
                if (len < 4)
                    return fail("bad request");
                int level = ReadI32(payload);
                bool good = HardwareSetPpm(level);
                std::string msg = good ? "PPM " + std::to_string(level)
                                       : "PPM " + std::to_string(level) +
                                             " 失败";
                return ok(ToWire(SimpleResult{good, std::move(msg)}));
            }
            case RequestType::SetTouchpad: {
                if (len < 1)
                    return fail("bad request");
                bool on = payload[0] != 0;
                bool good = HardwareSetTouchpad(on);
                std::string msg;
                if (good)
                    msg = on ? "触控板已开启"
                             : "触控板已关闭";
                else
                    msg = "触控板设置失败";
                return ok(ToWire(SimpleResult{good, std::move(msg)}));
            }
            case RequestType::SetPerfMode: {
                if (len < 4)
                    return fail("bad request");
                int mode = ReadI32(payload);
                bool good = HardwareSetPerfMode(mode);
                return ok(ToWire(SimpleResult{
                    good, good ? "已切换"
                               : "切换失败"}));
            }
            case RequestType::RunGpuFix: {
                bool skip = len > 0 && payload[0] != 0;
                GpuFixResult r{};
                HardwareGpuFix(skip, r);
                // Old HardwareApi.RunGpuFix re-derives ok/smi from message text:
                // ok = rc ok; smiFound = !contains("鎵句笉鍒?).
                std::string log = r.log;
                bool noSmi = log.find("找不到") !=
                             std::string::npos;
                GpuFixResult wire{};
                wire.nvapiStatus = (r.nvapiStatus == 0) ? 0 : -1;
                wire.clockResetOk = (log.find("-rgc") != std::string::npos) ? 1 : 0;
                wire.smiFound = noSmi ? 0 : 1;
                wire.log = std::move(log);
                return ok(ToWire(wire));
            }
            case RequestType::GetGpuStats: {
                GpuStatsWire g{-1, -1};
                GpuStatsWire q{};
                if (HardwareGetGpuStats(q))
                    g = q;
                return ok(ToWire(g));
            }
            case RequestType::GetOcInfo: {
                std::string err;
                bool avail = HardwareOcAvailable(err);
                OcInfo o{};
                o.available = avail;
                // C#: new(NvPstatesOc.IsAvailable(), NvPstatesOc.LastError)
                // LastError is null on success, message on failure.
                if (!err.empty()) {
                    o.hasLastError = true;
                    o.lastError = std::move(err);
                }
                return ok(ToWire(o));
            }
            case RequestType::ApplyOcOffset: {
                JsonValue root;
                if (len > 0 && !ParseJson(payload, len, root, nullptr))
                    return fail("bad request");
                int domain = 0, offset = 0;
                FromWireOc(root, domain, offset);
                int rc = HardwareOcApply(domain, offset);
                if (rc == 0)
                    return ok(ToWire(SimpleResult{true, "已应用"}));
                std::string oerr;
                HardwareOcAvailable(oerr);
                std::string msg;
                if (rc == -1 || rc == -2) {
                    msg = "NVAPI 不可用（" +
                          (oerr.empty() ? "rc=" + std::to_string(rc) : oerr) +
                          "）";
                } else {
                    msg = "失败(rc=" + std::to_string(rc) + ")";
                }
                return ok(ToWire(SimpleResult{false, std::move(msg)}));
            }
            case RequestType::GetSettings: {
                std::string json = "{\"tempPollSeconds\":" +
                                   std::to_string(settings_.tempPollSeconds()) + "}";
                return ok(json);
            }
            case RequestType::SetSettings: {
                JsonValue root;
                if (len > 0 && ParseJson(payload, len, root, nullptr))
                    settings_.update(FromWireSettings(root));
                return ok(ToWire(SimpleResult{true, "已保存"}));
            }
            case RequestType::GetTriggers:
                return ok(ToWireTriggers(triggers_.asDtos()));
            case RequestType::SaveTriggers: {
                JsonValue root;
                if (len > 0 && !ParseJson(payload, len, root, nullptr))
                    return fail("bad request");
                triggers_.replace(FromWireTriggerList(root));
                return ok(ToWire(SimpleResult{true, "已保存"}));
            }
            case RequestType::RunTriggerAction:
                // Declared but never sent/handled by the old service either.
                return fail("未知请求");
            default:
                return fail("未知请求");
        }
    } catch (...) {
        return fail("internal error");
    }
}

}  // namespace svc
