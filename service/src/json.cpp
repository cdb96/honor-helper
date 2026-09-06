// Minimal UTF-8 JSON parser/serializer (compact object implementation kept
// inline so every service TU can share the wire/store logic without extra deps).
#include "json.h"

#include <cctype>
#include <cstdlib>
#include <cstring>

namespace svc {

const JsonValue* JsonValue::find(const std::string& key) const {
    if (type != Type::Obj)
        return nullptr;
    for (const auto& kv : obj)
        if (kv.first == key)
            return &kv.second;
    return nullptr;
}

const JsonValue* JsonValue::findI(const std::string& key) const {
    if (type != Type::Obj)
        return nullptr;
    for (const auto& kv : obj) {
        if (kv.first.size() != key.size())
            continue;
        bool same = true;
        for (size_t i = 0; i < key.size(); ++i) {
            unsigned char a = (unsigned char)kv.first[i];
            unsigned char b = (unsigned char)key[i];
            if (a >= 'A' && a <= 'Z')
                a = (unsigned char)(a + 32);
            if (b >= 'A' && b <= 'Z')
                b = (unsigned char)(b + 32);
            if (a != b) {
                same = false;
                break;
            }
        }
        if (same)
            return &kv.second;
    }
    return nullptr;
}

bool JsonValue::asBool(bool def) const {
    if (type == Type::Bool)
        return boolean;
    if (type == Type::Number)
        return number != "0";
    return def;
}

long long JsonValue::asInt(long long def) const {
    if (type == Type::Number) {
        char* end = nullptr;
        long long v = std::strtoll(number.c_str(), &end, 10);
        return (end && *end == 0) ? v : def;
    }
    if (type == Type::Bool)
        return boolean ? 1 : 0;
    return def;
}

std::string JsonValue::asString(const std::string& def) const {
    return type == Type::Str ? str : def;
}

namespace {

struct Cursor {
    const char* p;
    const char* end;
    std::string* error;
    void fail(const char* msg) {
        if (error && error->empty()) {
            size_t off = (size_t)(p > end ? end - p : p - (const char*)0);
            *error = msg;
            (void)off;
        }
    }
    void skipWs() {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r'))
            ++p;
    }
    bool eof() const { return p >= end; }
    char peek() const { return eof() ? 0 : *p; }
};

bool ParseValue(Cursor& c, JsonValue& out);
void AppendUtf8(std::string& s, unsigned cp) {
    if (cp < 0x80) {
        s.push_back((char)cp);
    } else if (cp < 0x800) {
        s.push_back((char)(0xC0 | (cp >> 6)));
        s.push_back((char)(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        s.push_back((char)(0xE0 | (cp >> 12)));
        s.push_back((char)(0x80 | ((cp >> 6) & 0x3F)));
        s.push_back((char)(0x80 | (cp & 0x3F)));
    } else {
        s.push_back((char)(0xF0 | (cp >> 18)));
        s.push_back((char)(0x80 | ((cp >> 12) & 0x3F)));
        s.push_back((char)(0x80 | ((cp >> 6) & 0x3F)));
        s.push_back((char)(0x80 | (cp & 0x3F)));
    }
}

bool ParseString(Cursor& c, std::string& out) {
    if (c.peek() != '"') {
        c.fail("expected string");
        return false;
    }
    ++c.p;  // opening quote
    out.clear();
    while (!c.eof()) {
        char ch = *c.p++;
        if (ch == '"')
            return true;
        if (ch == '\\') {
            if (c.eof()) {
                c.fail("truncated escape");
                return false;
            }
            char e = *c.p++;
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    if (c.end - c.p < 4) {
                        c.fail("truncated \\u escape");
                        return false;
                    }
                    unsigned cp = 0;
                    for (int i = 0; i < 4; ++i) {
                        char h = c.p[i];
                        cp <<= 4;
                        if (h >= '0' && h <= '9')
                            cp |= (unsigned)(h - '0');
                        else if (h >= 'a' && h <= 'f')
                            cp |= (unsigned)(h - 'a' + 10);
                        else if (h >= 'A' && h <= 'F')
                            cp |= (unsigned)(h - 'A' + 10);
                        else {
                            c.fail("bad \\u escape");
                            return false;
                        }
                    }
                    c.p += 4;
                    // surrogate pair?
                    if (cp >= 0xD800 && cp <= 0xDBFF && c.end - c.p >= 6 && c.p[0] == '\\' &&
                        c.p[1] == 'u') {
                        unsigned lo = 0;
                        bool ok = true;
                        for (int i = 0; i < 4; ++i) {
                            char h = c.p[2 + i];
                            lo <<= 4;
                            if (h >= '0' && h <= '9')
                                lo |= (unsigned)(h - '0');
                            else if (h >= 'a' && h <= 'f')
                                lo |= (unsigned)(h - 'a' + 10);
                            else if (h >= 'A' && h <= 'F')
                                lo |= (unsigned)(h - 'A' + 10);
                            else
                                ok = false;
                        }
                        if (ok && lo >= 0xDC00 && lo <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            c.p += 6;
                        }
                    }
                    AppendUtf8(out, cp);
                    break;
                }
                default:
                    c.fail("bad escape");
                    return false;
            }
        } else {
            out.push_back(ch);
        }
    }
    c.fail("unterminated string");
    return false;
}

bool ParseArray(Cursor& c, JsonValue& out) {
    ++c.p;  // '['
    out = JsonValue::ArrV();
    c.skipWs();
    if (c.peek() == ']') {
        ++c.p;
        return true;
    }
    for (;;) {
        JsonValue el;
        c.skipWs();
        if (!ParseValue(c, el))
            return false;
        out.arr.push_back(std::move(el));
        c.skipWs();
        char ch = c.peek();
        if (ch == ',') {
            ++c.p;
            continue;
        }
        if (ch == ']') {
            ++c.p;
            return true;
        }
        c.fail("expected ',' or ']'");
        return false;
    }
}

bool ParseObject(Cursor& c, JsonValue& out) {
    ++c.p;  // '{'
    out = JsonValue::ObjV();
    c.skipWs();
    if (c.peek() == '}') {
        ++c.p;
        return true;
    }
    for (;;) {
        c.skipWs();
        std::string key;
        if (!ParseString(c, key))
            return false;
        c.skipWs();
        if (c.peek() != ':') {
            c.fail("expected ':'");
            return false;
        }
        ++c.p;
        c.skipWs();
        JsonValue val;
        if (!ParseValue(c, val))
            return false;
        out.obj.emplace_back(std::move(key), std::move(val));
        c.skipWs();
        char ch = c.peek();
        if (ch == ',') {
            ++c.p;
            continue;
        }
        if (ch == '}') {
            ++c.p;
            return true;
        }
        c.fail("expected ',' or '}'");
        return false;
    }
}

bool ParseLiteral(Cursor& c, const char* word, JsonValue v, JsonValue& out) {
    size_t n = std::strlen(word);
    if ((size_t)(c.end - c.p) < n || std::memcmp(c.p, word, n) != 0) {
        c.fail("bad literal");
        return false;
    }
    c.p += n;
    out = std::move(v);
    return true;
}

bool ParseNumber(Cursor& c, JsonValue& out) {
    const char* start = c.p;
    if (c.peek() == '-')
        ++c.p;
    bool any = false;
    while (!c.eof() && std::isdigit((unsigned char)c.peek())) {
        ++c.p;
        any = true;
    }
    if (!c.eof() && c.peek() == '.') {
        ++c.p;
        while (!c.eof() && std::isdigit((unsigned char)c.peek())) {
            ++c.p;
            any = true;
        }
    }
    if (!c.eof() && (c.peek() == 'e' || c.peek() == 'E')) {
        ++c.p;
        if (!c.eof() && (c.peek() == '+' || c.peek() == '-'))
            ++c.p;
        bool expAny = false;
        while (!c.eof() && std::isdigit((unsigned char)c.peek())) {
            ++c.p;
            expAny = true;
        }
        if (!expAny) {
            c.fail("bad number");
            return false;
        }
    }
    if (!any) {
        c.fail("bad number");
        return false;
    }
    out = JsonValue::Num(std::string(start, c.p));
    return true;
}

bool ParseValue(Cursor& c, JsonValue& out) {
    c.skipWs();
    char ch = c.peek();
    if (ch == '"') {
        std::string s;
        if (!ParseString(c, s))
            return false;
        out = JsonValue::StrV(std::move(s));
        return true;
    }
    if (ch == '{')
        return ParseObject(c, out);
    if (ch == '[')
        return ParseArray(c, out);
    if (ch == 't')
        return ParseLiteral(c, "true", JsonValue::Bool(true), out);
    if (ch == 'f')
        return ParseLiteral(c, "false", JsonValue::Bool(false), out);
    if (ch == 'n')
        return ParseLiteral(c, "null", JsonValue::Null(), out);
    if (ch == '-' || std::isdigit((unsigned char)ch))
        return ParseNumber(c, out);
    c.fail("unexpected value");
    return false;
}

}  // namespace

bool ParseJson(const char* data, size_t len, JsonValue& out, std::string* error) {
    Cursor c{data, data + len, error};
    out = JsonValue::Null();
    if (!ParseValue(c, out))
        return false;
    c.skipWs();
    if (!c.eof()) {
        if (error && error->empty())
            *error = "trailing data";
        return false;
    }
    return true;
}

std::string QuoteJson(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 2);
    o.push_back('"');
    for (unsigned char ch : s) {
        switch (ch) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\b': o += "\\b"; break;
            case '\f': o += "\\f"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (ch < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", ch);
                    o += buf;
                } else {
                    o.push_back((char)ch);
                }
        }
    }
    o.push_back('"');
    return o;
}

std::string ToJson(const JsonValue& v) {
    switch (v.type) {
        case JsonValue::Type::Null:
            return "null";
        case JsonValue::Type::Bool:
            return v.boolean ? "true" : "false";
        case JsonValue::Type::Number:
            return v.number.empty() ? "0" : v.number;
        case JsonValue::Type::Str:
            return QuoteJson(v.str);
        case JsonValue::Type::Arr: {
            std::string o = "[";
            for (size_t i = 0; i < v.arr.size(); ++i) {
                if (i)
                    o.push_back(',');
                o += ToJson(v.arr[i]);
            }
            o.push_back(']');
            return o;
        }
        case JsonValue::Type::Obj: {
            std::string o = "{";
            for (size_t i = 0; i < v.obj.size(); ++i) {
                if (i)
                    o.push_back(',');
                o += QuoteJson(v.obj[i].first);
                o.push_back(':');
                o += ToJson(v.obj[i].second);
            }
            o.push_back('}');
            return o;
        }
    }
    return "null";
}

}  // namespace svc
