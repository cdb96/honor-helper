#pragma once
// Minimal UTF-8 JSON parser / serializer for the service.
// No external dependencies: only the pipe wire DTOs and the on-disk
// settings/triggers files are handled here.
#include <string>
#include <utility>
#include <vector>

namespace svc {

struct JsonValue {
    enum class Type { Null, Bool, Number, Str, Arr, Obj };
    Type type = Type::Null;
    bool boolean = false;
    std::string number;  // raw literal for Number (preserves unknown values exactly)
    std::string str;     // decoded UTF-8 for Str
    std::vector<JsonValue> arr;
    // Ordered object members; keys are decoded strings.
    std::vector<std::pair<std::string, JsonValue>> obj;

    static JsonValue Null() { return JsonValue(); }
    static JsonValue Bool(bool b) { JsonValue v; v.type = Type::Bool; v.boolean = b; return v; }
    static JsonValue Num(std::string raw) { JsonValue v; v.type = Type::Number; v.number = std::move(raw); return v; }
    static JsonValue StrV(std::string s) { JsonValue v; v.type = Type::Str; v.str = std::move(s); return v; }
    static JsonValue ArrV() { JsonValue v; v.type = Type::Arr; return v; }
    static JsonValue ObjV() { JsonValue v; v.type = Type::Obj; return v; }

    bool isNull() const { return type == Type::Null; }
    const JsonValue* find(const std::string& key) const;
    const JsonValue* findI(const std::string& key) const;  // ASCII case-insensitive
    bool asBool(bool def = false) const;
    long long asInt(long long def = 0) const;
    std::string asString(const std::string& def = "") const;
};

// Parse one JSON document. Returns false on syntax error (error text optional).
bool ParseJson(const char* data, size_t len, JsonValue& out, std::string* error = nullptr);
inline bool ParseJson(const std::string& s, JsonValue& out, std::string* error = nullptr) {
    return ParseJson(s.data(), s.size(), out, error);
}

// Serialize back to compact JSON (used for replies and unknown-field round-trip).
std::string ToJson(const JsonValue& v);

// Quote + escape one string (returns the quoted form).
std::string QuoteJson(const std::string& s);

}  // namespace svc
