// SettingsStore + TriggerStore: same files, same shapes, same defaults as the
// C# versions (PascalCase keys on disk, indented output, tolerant reads).
#include "stores.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cwchar>

namespace svc {

namespace {

std::wstring Widen(const std::string& u8) {
    if (u8.empty())
        return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, u8.data(), (int)u8.size(), nullptr, 0);
    std::wstring w((size_t)(n > 0 ? n : 0), L'\0');
    if (n > 0)
        MultiByteToWideChar(CP_UTF8, 0, u8.data(), (int)u8.size(), w.data(), n);
    return w;
}

std::string Narrow(const std::wstring& w) {
    if (w.empty())
        return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0,
                                nullptr, nullptr);
    std::string s((size_t)(n > 0 ? n : 0), '\0');
    if (n > 0)
        WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n,
                            nullptr, nullptr);
    return s;
}

std::wstring Join(const std::wstring& a, const std::wstring& b) {
    if (!a.empty() && (a.back() == L'\\' || a.back() == L'/'))
        return a + b;
    return a + L"\\" + b;
}

bool ReadFileU8(const std::wstring& path, std::string& out) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0 ||
        size.QuadPart > (LONGLONG)kMaxMessage) {
        CloseHandle(h);
        return false;
    }
    out.assign((size_t)size.QuadPart, '\0');
    size_t off = 0;
    while (off < out.size()) {
        DWORD chunk = (DWORD)((out.size() - off > 1 << 20) ? 1 << 20 : out.size() - off);
        DWORD got = 0;
        if (!ReadFile(h, out.data() + off, chunk, &got, nullptr) || got == 0)
            break;
        off += got;
    }
    CloseHandle(h);
    out.resize(off);
    return true;
}

bool WriteFileU8(const std::wstring& path, const std::string& data) {
    // Create parent first (best-effort); write via temp+replace for crash safety.
    std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return false;
    size_t off = 0;
    bool ok = true;
    while (off < data.size()) {
        DWORD chunk =
            (DWORD)((data.size() - off > 1 << 20) ? 1 << 20 : data.size() - off);
        DWORD wrote = 0;
        if (!WriteFile(h, data.data() + off, chunk, &wrote, nullptr) || wrote == 0) {
            ok = false;
            break;
        }
        off += wrote;
    }
    FlushFileBuffers(h);
    CloseHandle(h);
    if (!ok) {
        DeleteFileW(tmp.c_str());
        return false;
    }
    if (!MoveFileExW(tmp.c_str(), path.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

}  // namespace

std::wstring DataDirW() {
    wchar_t* local = nullptr;
    size_t n = 0;
    if (_wdupenv_s(&local, &n, L"LOCALAPPDATA") != 0 || !local) {
        wchar_t buf[MAX_PATH]{};
        DWORD len = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
        std::wstring root = len ? std::wstring(buf, len) : L".";
        std::wstring dir = Join(root, L"honor-helper");
        CreateDirectoryW(dir.c_str(), nullptr);
        return dir;
    }
    std::wstring dir = Join(local, L"honor-helper");
    // Legacy migration: HonorPerf -> honor-helper (once, best-effort).
    std::wstring legacy = Join(local, L"HonorPerf");
    free(local);
    DWORD attrDir = GetFileAttributesW(dir.c_str());
    DWORD attrLegacy = GetFileAttributesW(legacy.c_str());
    bool dirExists = attrDir != INVALID_FILE_ATTRIBUTES && (attrDir & FILE_ATTRIBUTE_DIRECTORY);
    bool legacyExists =
        attrLegacy != INVALID_FILE_ATTRIBUTES && (attrLegacy & FILE_ATTRIBUTE_DIRECTORY);
    if (!dirExists && legacyExists)
        MoveFileW(legacy.c_str(), dir.c_str());
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

std::string DataDirU8() { return Narrow(DataDirW()); }

std::string EncodeSettingsFile(int tempPollSeconds) {
    return std::string("{\r\n  \"TempPollSeconds\": ") +
           std::to_string(tempPollSeconds) + "\r\n}";
}

// Pretty-print with 2-space indent (C# WriteIndented style, \r\n-agnostic: LF).
std::string Pretty(const JsonValue& v, int indent);

namespace {
void PrettyInto(const JsonValue& v, int indent, std::string& o) {
    switch (v.type) {
        case JsonValue::Type::Null:
            o += "null";
            return;
        case JsonValue::Type::Bool:
            o += v.boolean ? "true" : "false";
            return;
        case JsonValue::Type::Number:
            o += v.number.empty() ? "0" : v.number;
            return;
        case JsonValue::Type::Str:
            o += QuoteJson(v.str);
            return;
        case JsonValue::Type::Arr: {
            if (v.arr.empty()) {
                o += "[]";
                return;
            }
            std::string pad((size_t)(indent + 1) * 2, ' ');
            std::string end((size_t)indent * 2, ' ');
            o += "[\n";
            for (size_t i = 0; i < v.arr.size(); ++i) {
                o += pad;
                PrettyInto(v.arr[i], indent + 1, o);
                if (i + 1 < v.arr.size())
                    o.push_back(',');
                o.push_back('\n');
            }
            o += end;
            o.push_back(']');
            return;
        }
        case JsonValue::Type::Obj: {
            if (v.obj.empty()) {
                o += "{}";
                return;
            }
            std::string pad((size_t)(indent + 1) * 2, ' ');
            std::string end((size_t)indent * 2, ' ');
            o += "{\n";
            for (size_t i = 0; i < v.obj.size(); ++i) {
                o += pad;
                o += QuoteJson(v.obj[i].first);
                o += ": ";
                PrettyInto(v.obj[i].second, indent + 1, o);
                if (i + 1 < v.obj.size())
                    o.push_back(',');
                o.push_back('\n');
            }
            o += end;
            o.push_back('}');
            return;
        }
    }
}
}  // namespace

std::string Pretty(const JsonValue& v, int indent) {
    std::string o;
    PrettyInto(v, indent, o);
    return o;
}

// Build one trigger object, preserving unknown keys: known fields refresh from
// the dto (so wire<->disk stay in sync), extra keys survive verbatim.
JsonValue TriggerToRaw(const Trigger& t) {
    JsonValue o = JsonValue::ObjV();
    auto set = [&](const char* key, JsonValue v) {
        for (auto& kv : o.obj)
            if (kv.first == key) {
                kv.second = std::move(v);
                return;
            }
        o.obj.emplace_back(key, std::move(v));
    };
    if (t.hasRaw && t.raw.type == JsonValue::Type::Obj) {
        o = t.raw;  // keep key order + unknown fields
    } else {
        // Fresh object: canonical key order (matches ProgramTrigger property order).
        o.obj = {
            {"Path", JsonValue::Null()},         {"OpenAction", JsonValue::Null()},
            {"OpenPpm", JsonValue::Null()},      {"CloseAction", JsonValue::Null()},
            {"OpenTouchpad", JsonValue::Null()}, {"CloseTouchpad", JsonValue::Null()},
            {"GpuCoreMhz", JsonValue::Null()},   {"GpuMemMhz", JsonValue::Null()},
            {"GpuFixOnExit", JsonValue::Null()}, {"Enabled", JsonValue::Null()},
        };
    }
    const TriggerDtoWire& d = t.dto;
    set("Path", JsonValue::StrV(d.path));
    set("OpenAction", JsonValue::StrV(d.openAction));
    set("OpenPpm", JsonValue::Num(std::to_string(d.openPpm)));
    set("CloseAction", JsonValue::StrV(d.closeAction));
    set("OpenTouchpad", JsonValue::StrV(d.openTouchpad));
    set("CloseTouchpad", JsonValue::StrV(d.closeTouchpad));
    set("GpuCoreMhz", JsonValue::Num(std::to_string(d.gpuCoreMhz)));
    set("GpuMemMhz", JsonValue::Num(std::to_string(d.gpuMemMhz)));
    set("GpuFixOnExit", JsonValue::Bool(d.gpuFixOnExit));
    set("Enabled", JsonValue::Bool(d.enabled));
    return o;
}

std::string EncodeTriggersFile(const std::vector<Trigger>& triggers) {
    JsonValue arr = JsonValue::ArrV();
    for (const auto& t : triggers)
        arr.arr.push_back(TriggerToRaw(t));
    return Pretty(arr, 0);
}

// ---- SettingsStore ----

SettingsStore::SettingsStore() { load(); }

int SettingsStore::tempPollSeconds() const {
    std::lock_guard<std::mutex> lk(mu_);
    return tempPollSeconds_;
}

void SettingsStore::update(int v) {
    std::lock_guard<std::mutex> lk(mu_);
    tempPollSeconds_ = v < 1 ? 1 : v;
    saveLocked();
}

void SettingsStore::load() {
    tempPollSeconds_ = kDefaultTempPollSeconds;
    std::string text;
    if (!ReadFileU8(Join(DataDirW(), L"settings.json"), text) || text.empty())
        return;
    JsonValue root;
    if (!ParseJson(text, root, nullptr) || root.type != JsonValue::Type::Obj)
        return;
    // Accept both PascalCase (disk) and camelCase (wire) spellings.
    const JsonValue* v = root.find("TempPollSeconds");
    if (!v)
        v = root.findI("tempPollSeconds");
    if (!v)
        return;
    long long n = v->asInt(kDefaultTempPollSeconds);
    tempPollSeconds_ = (int)(n < 1 ? 1 : n);
}

void SettingsStore::saveLocked() const {
    WriteFileU8(Join(DataDirW(), L"settings.json"),
                EncodeSettingsFile(tempPollSeconds_));
}

// ---- TriggerStore ----

TriggerStore::TriggerStore() { load(); }

std::vector<TriggerDtoWire> TriggerStore::asDtos() const {
    std::lock_guard<std::mutex> lk(mu_);
    std::vector<TriggerDtoWire> out;
    out.reserve(triggers_.size());
    for (const auto& t : triggers_)
        out.push_back(t.dto);
    return out;
}

std::vector<Trigger> TriggerStore::snapshot() const {
    std::lock_guard<std::mutex> lk(mu_);
    return triggers_;
}

void TriggerStore::replace(std::vector<TriggerDtoWire> items) {
    std::lock_guard<std::mutex> lk(mu_);
    triggers_.clear();
    triggers_.reserve(items.size());
    for (auto& d : items) {
        Trigger t;
        t.dto = std::move(d);
        if (t.dto.openAction.empty())
            t.dto.openAction = "none";
        if (t.dto.closeAction.empty())
            t.dto.closeAction = "none";
        if (t.dto.openTouchpad.empty())
            t.dto.openTouchpad = "none";
        if (t.dto.closeTouchpad.empty())
            t.dto.closeTouchpad = "none";
        triggers_.push_back(std::move(t));
    }
    saveLocked();
}

void TriggerStore::load() {
    triggers_.clear();
    std::string text;
    if (!ReadFileU8(Join(DataDirW(), L"triggers.json"), text) || text.empty())
        return;
    JsonValue root;
    if (!ParseJson(text, root, nullptr) || root.type != JsonValue::Type::Arr)
        return;
    for (const auto& el : root.arr) {
        if (el.type != JsonValue::Type::Obj)
            continue;
        Trigger t;
        FromWireTrigger(el, t.dto);
        t.raw = el;
        t.hasRaw = true;
        triggers_.push_back(std::move(t));
    }
}

void TriggerStore::saveLocked() const {
    WriteFileU8(Join(DataDirW(), L"triggers.json"), EncodeTriggersFile(triggers_));
}

}  // namespace svc
