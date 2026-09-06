#pragma once
// Program-linkage engine: polls the process list every 3s, fires open/close
// edges (perf mode / touchpad / GPU OC / gpu-fix-on-exit), same as TriggerEngine.cs.
#include <windows.h>

#include <atomic>
#include <set>
#include <string>

namespace svc {

class TriggerStore;

class TriggerEngine {
public:
    explicit TriggerEngine(TriggerStore& store);
    ~TriggerEngine();
    void start();
    void stop();

private:
    TriggerStore& store_;
    std::atomic<bool> stop_{false};
    HANDLE thread_ = nullptr;
    HANDLE wake_ = nullptr;  // signaled on stop so the 3s sleep ends early
    std::set<std::wstring> running_;  // lower-cased exe base names, no extension

    static DWORD WINAPI ThreadMain(LPVOID self);
    void loop();
    void scanOnce();
    static void RunTrigger(const TriggerStore& store, std::wstring exeBaseLower,
                           bool opening);

    TriggerEngine(const TriggerEngine&) = delete;
    TriggerEngine& operator=(const TriggerEngine&) = delete;
};

}  // namespace svc
