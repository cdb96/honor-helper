#pragma once
// Persistent settings + trigger stores (mirrors common/ + service/*.cs).
// Files live in %LOCALAPPDATA%\honor-helper\ (with HonorPerf legacy migration).
// JSON arrays keep unknown fields (raw JsonValue) so rewrite-after-read
// never drops data a newer/older build may own.
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "json.h"
#include "protocol.h"

namespace svc {

std::wstring DataDirW();  // creates the dir, migrates HonorPerf once
std::string DataDirU8();

// ---- settings ----

inline const int kDefaultTempPollSeconds = 5;

class SettingsStore {
public:
    SettingsStore();
    int tempPollSeconds() const;
    void update(int tempPollSeconds);  // clamps >= 1, persists

private:
    mutable std::mutex mu_;
    int tempPollSeconds_ = kDefaultTempPollSeconds;
    void load();
    void saveLocked() const;
};

// ---- triggers ----

struct Trigger {
    TriggerDtoWire dto;
    // Raw JSON object from disk (keeps unknown keys + original value shapes).
    JsonValue raw = JsonValue::ObjV();
    bool hasRaw = false;
};

class TriggerStore {
public:
    TriggerStore();
    std::vector<TriggerDtoWire> asDtos() const;
    std::vector<Trigger> snapshot() const;  // full copy incl. raw payloads
    void replace(std::vector<TriggerDtoWire> items);

private:
    mutable std::mutex mu_;
    std::vector<Trigger> triggers_;
    void load();
    void saveLocked() const;
};

// Encode the on-disk triggers.json array (indented, like the C# output).
std::string EncodeTriggersFile(const std::vector<Trigger>& triggers);
// settings.json body: {"TempPollSeconds":N} (PascalCase, indented, like C#).
std::string EncodeSettingsFile(int tempPollSeconds);

}  // namespace svc
