#pragma once
// Named-pipe server: medium-integrity pipe (same SDDL as the C# build) +
// one-shot per-connection dispatch to hardware/stores.
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

namespace svc {

class SettingsStore;
class TriggerStore;

class PipeServer {
public:
    PipeServer(SettingsStore& settings, TriggerStore& triggers);
    ~PipeServer();
    void start();
    void stop();  // wakes the blocked listener via a cancel pipe connection

private:
    SettingsStore& settings_;
    TriggerStore& triggers_;
    std::atomic<bool> stop_{false};
    HANDLE listenerThread_ = nullptr;
    HANDLE readyEvent_ = nullptr;

    static DWORD WINAPI ListenerMain(LPVOID self);
    void listenerLoop();
    void handleOne(HANDLE pipe);
    std::vector<char> dispatch(int32_t kind, const char* payload, size_t len);

    // Raw reads/writes (loop until complete or broken).
    static bool ReadFull(HANDLE h, char* buf, size_t n);
    static bool WriteFull(HANDLE h, const char* buf, size_t n);

    PipeServer(const PipeServer&) = delete;
    PipeServer& operator=(const PipeServer&) = delete;
};

}  // namespace svc
