/*
 * honor_core.cpp — native implementation of the honor-helper core.
 *
 * Ports the managed logic from the former HonorHelper.Core / Data classes
 * into native C++ and exposes a flat C ABI (see honor_core.h).
 *
 * Everything here owns actual hardware interaction:
 *   - HONOR WMI (ROOT\WMI\OemWMIMethod, OemWMIfun) via raw COM/WMI.
 *   - Windows perf counters (root\cimv2) for CPU frequency.
 *   - NVIDIA NVAPI (nvapi64.dll, dynamically bound) for GPU stats /
 *     dynamic-pstate unlock / SetPstates20 overclock.
 *   - nvidia-smi fallback and clock reset.
 *
 * The managed UI P/Invokes into this DLL; all public functions are safe to call
 * from any thread (they initialize COM as needed and serialize COM work through
 * one mutex). COM connections and the WMI instance path are cached for the
 * process lifetime so repeated polling does not reconnect/re-enumerate.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wbemidl.h>
#include <comdef.h>
#include <oleauto.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <mutex>

#include "honor_core.h"

#pragma comment(lib, "wbemuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

/* ------------------------------------------------------------------ */
/* COM smart-pointer typedefs                                          */
/* ------------------------------------------------------------------ */
_COM_SMARTPTR_TYPEDEF(IWbemLocator, __uuidof(IWbemLocator));
_COM_SMARTPTR_TYPEDEF(IWbemServices, __uuidof(IWbemServices));
_COM_SMARTPTR_TYPEDEF(IWbemClassObject, __uuidof(IWbemClassObject));
_COM_SMARTPTR_TYPEDEF(IEnumWbemClassObject, __uuidof(IEnumWbemClassObject));

namespace {

const wchar_t* kWmiNamespace = L"\\\\.\\ROOT\\WMI";
const wchar_t* kCimv2Namespace = L"\\\\.\\ROOT\\cimv2";
const wchar_t* kClassName = L"OemWMIMethod";
const wchar_t* kMethodName = L"OemWMIfun";

std::mutex g_mutex;

/* Cached COM connections + WMI instance path (process lifetime). */
IWbemServices* g_wmi = nullptr;
IWbemServices* g_cimv2 = nullptr;
std::wstring g_instancePath;

/* Diagnostic buffers (UTF-8) for the last operation. */
std::string g_log;
std::string g_ocError;

/* ------------------------------------------------------------------ */
/* COM helpers                                                         */
/* ------------------------------------------------------------------ */

HRESULT EnsureCom() {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    // S_FALSE = already initialized on this thread in the same mode (fine).
    // RPC_E_CHANGED_MODE = initialized in another mode (e.g. STA via WinUI);
    //   WMI objects created as free-threaded proxies still work from here.
    if (hr == S_FALSE || hr == RPC_E_CHANGED_MODE)
        return S_OK;
    return hr;
}

void TearDown(IWbemServices*& svc) {
    if (svc) {
        svc->Release();
        svc = nullptr;
    }
}

/* Connect to a namespace and apply the proxy authentication that HONOR's WMI
   needs (a raw ConnectServer without it is access-denied). */
IWbemServices* ConnectNamespace(const wchar_t* ns) {
    IWbemLocatorPtr locator;
    if (FAILED(CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IWbemLocator, (void**)&locator)))
        return nullptr;

    IWbemServicesPtr svc;
    if (FAILED(locator->ConnectServer(_bstr_t(ns), nullptr, nullptr, nullptr, 0,
                                      0, 0, &svc)))
        return nullptr;

    // WmiLight-equivalent auth: required for the HWMI virtual class.
    CoSetProxyBlanket(svc, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                      RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE,
                      nullptr, EOAC_NONE);

    svc.AddRef();           // hand ownership to the caller
    return svc.Detach();
}

/* Find the OemWMIMethod instance whose InstanceName contains "HWMI" and cache
   its relative object path. Returns false if not found. */
bool FindHwmiInstance(IWbemServices* svc, std::wstring& outPath) {
    IEnumWbemClassObjectPtr enumerator;
    if (FAILED(svc->CreateInstanceEnum(_bstr_t(kClassName), 0, nullptr, &enumerator)))
        return false;

    IWbemClassObjectPtr obj;
    ULONG returned = 0;
    while (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == S_OK &&
           returned == 1) {
        VARIANT v;
        VariantInit(&v);
        bool match = false;
        if (SUCCEEDED(obj->Get(L"InstanceName", 0, &v, nullptr, nullptr)) &&
            v.vt == VT_BSTR && v.bstrVal != nullptr) {
            std::wstring name(v.bstrVal ? v.bstrVal : L"");
            if (name.find(L"HWMI") != std::wstring::npos) {
                // Relative keyed object path; Escape the name for quotes.
                std::wstring escaped;
                for (wchar_t ch : name) {
                    if (ch == L'"' || ch == L'\\')
                        escaped.push_back(L'\\');
                    escaped.push_back(ch);
                }
                outPath = std::wstring(L"OemWMIMethod.InstanceName=\"") +
                          escaped + L"\"";
                match = true;
            }
        }
        VariantClear(&v);
        obj = nullptr;      // smart ptr releases
        if (match)
            return true;
    }
    return false;
}

IWbemServices* WmiService() {
    if (g_wmi == nullptr) {
        g_wmi = ConnectNamespace(kWmiNamespace);
        if (g_wmi == nullptr)
            return nullptr;
    }
    return g_wmi;
}

const std::wstring& InstancePath() {
    if (g_instancePath.empty()) {
        IWbemServices* svc = WmiService();
        if (svc != nullptr)
            FindHwmiInstance(svc, g_instancePath);
    }
    return g_instancePath;
}

void ResetWmi() {
    g_instancePath.clear();
    TearDown(g_wmi);
}

/* ------------------------------------------------------------------ */
/* The single OemWMIfun driver. Returns the raw u8Output bytes.        */
/* ------------------------------------------------------------------ */
bool InvokeCommand(const uint8_t* cmd, size_t cmdLen, std::vector<uint8_t>& out) {
    IWbemServices* svc = WmiService();
    if (svc == nullptr)
        return false;
    const std::wstring& path = InstancePath();
    if (path.empty())
        return false;

    IWbemClassObjectPtr pClass;
    if (FAILED(svc->GetObject(_bstr_t(kClassName), 0, nullptr, &pClass, nullptr)))
        return false;

    IWbemClassObjectPtr pInParams;
    if (FAILED(pClass->GetMethod(_bstr_t(kMethodName), 0, &pInParams, nullptr)))
        return false;

    IWbemClassObjectPtr pInput;
    if (FAILED(pInParams->SpawnInstance(0, &pInput)))
        return false;

    // u8Input is a fixed 64-byte buffer; CIM rejects shorter input.
    uint8_t buf[64];
    memset(buf, 0, sizeof(buf));
    if (cmdLen > 0)
        memcpy(buf, cmd, cmdLen > sizeof(buf) ? sizeof(buf) : cmdLen);

    SAFEARRAY* psa = SafeArrayCreateVector(VT_UI1, 0, 64);
    if (psa == nullptr)
        return false;
    {
        void* pv = nullptr;
        if (SafeArrayAccessData(psa, &pv) == S_OK && pv != nullptr) {
            memcpy(pv, buf, 64);
            SafeArrayUnaccessData(psa);
        }
    }

    VARIANT v;
    VariantInit(&v);
    v.vt = VT_ARRAY | VT_UI1;
    v.parray = psa;
    HRESULT putHr = pInput->Put(L"u8Input", 0, &v, 0);
    VariantClear(&v);       // frees the SAFEARRAY
    if (FAILED(putHr))
        return false;

    IWbemClassObjectPtr pOutput;
    if (FAILED(svc->ExecMethod(_bstr_t(path.c_str()), _bstr_t(kMethodName),
                               0, nullptr, pInput, &pOutput, nullptr)))
        return false;

    VARIANT outv;
    VariantInit(&outv);
    if (FAILED(pOutput->Get(L"u8Output", 0, &outv, nullptr, nullptr)))
        return false;

    out.clear();
    if ((outv.vt & VT_ARRAY) && (outv.vt & VT_UI1) && outv.parray != nullptr) {
        long lb = 0, ub = 0;
        if (SafeArrayGetLBound(outv.parray, 1, &lb) == S_OK &&
            SafeArrayGetUBound(outv.parray, 1, &ub) == S_OK) {
            long len = ub - lb + 1;
            if (len > 0) {
                out.resize((size_t)len);
                void* pv = nullptr;
                if (SafeArrayAccessData(outv.parray, &pv) == S_OK && pv != nullptr) {
                    memcpy(out.data(), pv, (size_t)len);
                    SafeArrayUnaccessData(outv.parray);
                }
            }
        }
    }
    VariantClear(&outv);
    return true;
}

/* One retry: drop a stale instance/connection and rebuild once. */
bool Invoke(const std::vector<uint8_t>& cmd, std::vector<uint8_t>& out) {
    std::lock_guard<std::mutex> lk(g_mutex);
    EnsureCom();                 // CoCreateInstance needs COM init on this thread
    for (int attempt = 0; attempt < 2; ++attempt) {
        if (attempt > 0)
            ResetWmi();
        if (InvokeCommand(cmd.data(), cmd.size(), out))
            return true;
    }
    return false;
}

/* Decode a u16 at @off from the output (little-endian first, then big-endian as
   a fallback). Returns -1 when the read is implausible/out of range. */
int DecodeRpm(const std::vector<uint8_t>& o, size_t off, int maxPlausible) {
    if (o.size() < off + 2)
        return -1;
    int le = (int)o[off] | ((int)o[off + 1] << 8);
    if (le <= maxPlausible)
        return le;
    int be = ((int)o[off] << 8) | (int)o[off + 1];
    return be <= maxPlausible ? be : -1;
}

/* ------------------------------------------------------------------ */
/* nvidia-smi capture                                                  */
/* ------------------------------------------------------------------ */

/* Run an executable with args, capturing stdout+stderr, with a timeout.
   Returns true if the process ran to completion (exit code captured). */
bool RunCapture(const std::wstring& file, const std::wstring& args,
                int timeoutMs, std::string& outText, bool& timedOut) {
    outText.clear();
    timedOut = false;

    // Build a mutable command line.
    std::wstring cmd = L"\"" + file + L"\" " + args;
    std::vector<wchar_t> cmdline(cmd.begin(), cmd.end());
    cmdline.push_back(L'\0');

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE readPipe = nullptr, writePipe = nullptr;
    if (!CreatePipe(&readPipe, &writePipe, &sa, 0))
        return false;
    SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = writePipe;
    si.hStdError = writePipe;   // merge stderr into stdout for -rgc

    PROCESS_INFORMATION pi{};
    BOOL ok = CreateProcessW(nullptr, cmdline.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(writePipe);     // parent doesn't need the write end
    if (!ok) {
        CloseHandle(readPipe);
        return false;
    }

    // Wait for the process, then drain the pipe. nvidia-smi emits only a few
    // bytes, so the read side cannot fill and deadlock the child.
    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wait == WAIT_TIMEOUT) {
        TerminateProcess(pi.hProcess, 1);
        timedOut = true;
    }

    char chunk[4096];
    DWORD readable = 0;
    while (readPipe != nullptr && ReadFile(readPipe, chunk, sizeof(chunk),
                                           &readable, nullptr) && readable > 0)
        outText.append(chunk, readable);

    CloseHandle(readPipe);
    CloseHandle(pi.hThread);

    if (timedOut) {
        CloseHandle(pi.hProcess);
        return false;
    }

    DWORD exitCode = 0;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);
    return exitCode == 0;
}

std::wstring FindNvidiaSmi() {
    wchar_t sysdir[MAX_PATH];
    if (GetSystemDirectoryW(sysdir, MAX_PATH) != 0) {
        std::wstring p = std::wstring(sysdir) + L"\\nvidia-smi.exe";
        if (GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES)
            return p;
    }
    std::wstring q = L"C:\\Program Files\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe";
    if (GetFileAttributesW(q.c_str()) != INVALID_FILE_ATTRIBUTES)
        return q;
    return std::wstring();
}

/* ------------------------------------------------------------------ */
/* NVAPI dynamic binding                                               */
/* ------------------------------------------------------------------ */

using NvApiQueryInterface_t = void* (__cdecl*)(unsigned int);
using NvApiInitialize_t = int (__cdecl*)();
using NvApiEnumGpus_t = int (__cdecl*)(void**, unsigned int*);
using NvApiEnableDynamicPstates_t = int (__cdecl*)(void*, unsigned int);
using NvApiGetFullName_t = int (__cdecl*)(void*, char*);
using NvApiSetPstates20_t = int (__cdecl*)(void*, void*);
using NvApiGetUsages_t = int (__cdecl*)(void*, void*);
using NvApiGetAllClocks_t = int (__cdecl*)(void*, void*);

const unsigned int kQiInitialize = 0x0150E828;
const unsigned int kQiEnumPhysicalGpus = 0xE5AC921F;
const unsigned int kQiEnableDynamicPstates = 0xFA579A0F;
const unsigned int kQiGetFullName = 0xCEEE8E9F;
const unsigned int kQiGetUsages = 0x189A1FDF;
const unsigned int kQiGetAllClocks = 0x1BD69F49;
const unsigned int kQiSetPstates20 = 0x0F4DAE6B;

void* NvApiResolve(NvApiQueryInterface_t qi, unsigned int id) {
    return qi == nullptr ? nullptr : qi(id);
}

}  // anonymous namespace

/* ================================================================== */
/* Exported C ABI                                                      */
/* ================================================================== */

extern "C" {

HONOR_API hc_status hc_init() {
    EnsureCom();
    return 0;
}

HONOR_API void hc_shutdown() {
    std::lock_guard<std::mutex> lk(g_mutex);
    TearDown(g_wmi);
    TearDown(g_cimv2);
    g_instancePath.clear();
}

HONOR_API int32_t hc_is_admin() {
    BOOL isAdmin = FALSE;
    SID_IDENTIFIER_AUTHORITY nt = SECURITY_NT_AUTHORITY;
    PSID adminGroup = nullptr;
    if (AllocateAndInitializeSid(&nt, 2, SECURITY_BUILTIN_DOMAIN_RID,
                                 DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0,
                                 &adminGroup)) {
        if (!CheckTokenMembership(nullptr, adminGroup, &isAdmin))
            isAdmin = FALSE;
        FreeSid(adminGroup);
    }
    return isAdmin ? 1 : 0;
}

HONOR_API int32_t hc_get_perf_mode() {
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{0x04, 0x0E}, o))
        return -1;
    if (o.size() < 2)
        return -1;
    if (o[0] != 0 && o[0] != 0xEE)
        return -1;
    return (int32_t)o[1];
}

HONOR_API int32_t hc_set_perf_mode(int32_t mode) {
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{
            0x04, 0x0F, (uint8_t)(mode & 0xFF)}, o))
        return 0;
    return (o.size() >= 1 && o[0] == 0) ? 1 : 0;
}

HONOR_API int32_t hc_set_ppm(int32_t level) {
    uint8_t lvl = (uint8_t)(level & 0xFF);
    std::vector<uint8_t> o;

    // 1. direct write.
    if (Invoke(std::vector<uint8_t>{0x07, 0x0D, lvl}, o) &&
        o.size() >= 1 && o[0] == 0)
        return 1;

    // 2. PPM is mode-gated -> switch to beast then retry.
    if (hc_set_perf_mode(3) != 1)
        return 0;
    Sleep(300);

    if (Invoke(std::vector<uint8_t>{0x07, 0x0D, lvl}, o) &&
        o.size() >= 1 && o[0] == 0)
        return 1;
    return 0;
}

HONOR_API int32_t hc_get_temp(int32_t channel) {
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{
            0x02, 0x02, (uint8_t)(channel & 0xFF)}, o))
        return -1;
    if (o.size() > 2 && o[0] == 0)
        return (int32_t)o[2];
    return -1;
}

HONOR_API int32_t hc_get_fan_speed(int32_t id, int32_t* out_limit) {
    if (out_limit != nullptr)
        *out_limit = -1;
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{0x02, 0x08, (uint8_t)(id & 0xFF)}, o))
        return -1;
    if (o.size() < 3 || o[0] != 0)
        return -1;
    int rpm = DecodeRpm(o, 1, 15000);
    if (rpm < 0)
        return -1;
    if (out_limit != nullptr)
        *out_limit = DecodeRpm(o, 3, 15000);
    return rpm;
}

HONOR_API int32_t hc_get_touchpad_state() {
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{0x02, 0x0F, 0xFF}, o))
        return -1;
    if (o.size() < 2 || o[0] != 0)
        return -1;
    if (o[1] != 0 && o[1] != 1)
        return -1;
    return (int32_t)o[1];
}

HONOR_API int32_t hc_set_touchpad_state(int32_t on) {
    std::vector<uint8_t> o;
    if (!Invoke(std::vector<uint8_t>{
            0x02, 0x10, (uint8_t)(on ? 1 : 0)}, o))
        return 0;
    return (o.size() >= 1 && o[0] == 0) ? 1 : 0;
}

HONOR_API int32_t hc_get_cpu_freq_mhz() {
    std::lock_guard<std::mutex> lk(g_mutex);
    EnsureCom();

    if (g_cimv2 == nullptr) {
        g_cimv2 = ConnectNamespace(kCimv2Namespace);
        if (g_cimv2 == nullptr)
            return -1;
    }

    BSTR wql = SysAllocString(L"WQL");
    BSTR query = SysAllocString(L"SELECT PercentProcessorPerformance, "
                                L"ProcessorFrequency FROM "
                                L"Win32_PerfFormattedData_Counters_ProcessorInformation "
                                L"WHERE Name='_Total'");
    IEnumWbemClassObjectPtr enumerator;
    g_cimv2->ExecQuery(wql, query, WBEM_FLAG_FORWARD_ONLY, nullptr, &enumerator);
    SysFreeString(wql);
    SysFreeString(query);
    if (enumerator == nullptr)
        return -1;

    int result = -1;
    IWbemClassObjectPtr obj;
    ULONG returned = 0;
    while (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == S_OK &&
           returned == 1) {
        auto readDouble = [&](const wchar_t* prop, double& outV) -> bool {
            VARIANT v, vt;
            VariantInit(&v);
            VariantInit(&vt);
            bool okv = false;
            if (SUCCEEDED(obj->Get(prop, 0, &v, nullptr, nullptr))) {
                if (v.vt == VT_I4 || v.vt == VT_UI4 || v.vt == VT_I8 ||
                    v.vt == VT_UI8 || v.vt == VT_R8) {
                    if (SUCCEEDED(VariantChangeType(&vt, &v, 0, VT_R8))) {
                        outV = vt.dblVal;
                        okv = true;
                    }
                }
                else if (v.vt == VT_BSTR && v.bstrVal != nullptr) {
                    // Some perf counters expose the value as a number string (BSTR).
                    outV = _wtof(v.bstrVal);
                    okv = true;
                }
            }
            VariantClear(&v);
            VariantClear(&vt);
            return okv;
        };

        double perf = 0, base = 0;
        bool havePerf = readDouble(L"PercentProcessorPerformance", perf);
        bool haveBase = readDouble(L"ProcessorFrequency", base);
        obj = nullptr;
        if (havePerf && haveBase && base > 0) {
            result = (int32_t)(base * perf / 100.0 + 0.5);
            break;
        }
    }
    return result;
}

HONOR_API int32_t hc_gpu_stats_query(hc_gpu_stats* out) {
    if (out == nullptr)
        return 0;
    out->clock_mhz = -1;
    out->util_pct = -1;

    // Prefer in-process NvAPI query; fall back to nvidia-smi.
    {
        // --- NvAPI in-process query ---
        HMODULE hmod = LoadLibraryW(L"nvapi64.dll");
        if (hmod != nullptr) {
            auto qifn = (NvApiQueryInterface_t)(void*)GetProcAddress(hmod, "nvapi_QueryInterface");
            if (qifn != nullptr) {
                auto init = (NvApiInitialize_t)NvApiResolve(qifn, kQiInitialize);
                NvApiEnumGpus_t eg = nullptr;
                NvApiGetUsages_t gu = nullptr;
                NvApiGetAllClocks_t gc = nullptr;
                void* gpu = nullptr;
                if (init != nullptr && init() == 0) {
                    eg = (NvApiEnumGpus_t)NvApiResolve(qifn, kQiEnumPhysicalGpus);
                    gu = (NvApiGetUsages_t)NvApiResolve(qifn, kQiGetUsages);
                    gc = (NvApiGetAllClocks_t)NvApiResolve(qifn, kQiGetAllClocks);
                    if (eg != nullptr && gu != nullptr && gc != nullptr) {
                        void* gpus[64] = {};
                        unsigned int count = 0;
                        if (eg(gpus, &count) == 0 && count > 0)
                            gpu = gpus[0];
                    }
                }
                if (gpu != nullptr) {
                    struct NvUsages { unsigned int version; unsigned int entries[33]; } u{};
                    struct NvClocks { unsigned int version; unsigned int clocks[64]; } c{};
                    u.version = (unsigned int)sizeof(u) | (1u << 16);
                    c.version = (unsigned int)sizeof(c) | (1u << 16);
                    if (gu(gpu, &u) == 0 && gc(gpu, &c) == 0) {
                        int util = (u.entries[2] > 100) ? 100 : (int)u.entries[2];
                        int mhz = (int)(c.clocks[0] / 1000u);
                        out->util_pct = util;
                        out->clock_mhz = mhz;
                        FreeLibrary(hmod);
                        return 1;
                    }
                }
            }
            FreeLibrary(hmod);
        }
    }

    // --- nvidia-smi fallback ---
    std::wstring smi = FindNvidiaSmi();
    if (smi.empty())
        return 0;
    std::string stdoutText;
    bool timedOut = false;
    if (!RunCapture(smi,
                    L"--query-gpu=clocks.current.graphics,utilization.gpu "
                    L"--format=csv,noheader,nounits",
                    5000, stdoutText, timedOut))
        return 0;

    int mhz = -1, util = -1;
    size_t comma = stdoutText.find(',');
    if (comma != std::string::npos) {
        mhz = atoi(stdoutText.c_str());
        util = atoi(stdoutText.c_str() + comma + 1);
    }
    if (mhz < 0 && util < 0)
        return 0;
    out->clock_mhz = mhz;
    out->util_pct = util < 0 ? -1 : (util > 100 ? 100 : util);
    return 1;
}

HONOR_API int32_t hc_gpu_clockfix_run(int32_t skip_clock_reset, hc_gpu_fix* out) {
    std::lock_guard<std::mutex> lk(g_mutex);
    g_log.clear();
    g_log.reserve(1024);

    hc_gpu_fix info{};
    info.nvapi_status = -999;
    info.clock_reset_ok = 0;
    info.smi_found = 0;

    // Step 1: NvAPI_GPU_EnableDynamicPstates(1).
    HMODULE hmod = LoadLibraryW(L"nvapi64.dll");
    if (hmod == nullptr) {
        g_log += "LoadLibrary(nvapi64.dll) 失败（无 NVIDIA 驱动？）\n";
        info.nvapi_status = -100;
    } else {
        auto qifn = (NvApiQueryInterface_t)(void*)GetProcAddress(hmod, "nvapi_QueryInterface");
        if (qifn == nullptr) {
            g_log += "QI init null\n";
            info.nvapi_status = -103;
        } else {
            auto init = (NvApiInitialize_t)NvApiResolve(qifn, kQiInitialize);
            int rc = -200;
            if (init == nullptr) {
                g_log += "QI Init null\n";
                rc = -103;
            } else {
                rc = init();
                char line[80];
                sprintf_s(line, "NvAPI_Initialize => %d\n", rc);
                g_log += line;
                if (rc == 0) {
                    auto dyn = (NvApiEnableDynamicPstates_t)NvApiResolve(qifn, kQiEnableDynamicPstates);
                    auto getName = (NvApiGetFullName_t)NvApiResolve(qifn, kQiGetFullName);
                    unsigned int probes[] = { 0xE5AC921Fu, 0x33C7358Cu,
                                              0xAD298D3Fu, 0xD22BDD7Eu };
                    bool triedAny = false;
                    info.nvapi_status = -200;
                    for (auto pid : probes) {
                        void* p = NvApiResolve(qifn, pid);
                        char probeLine[80];
                        sprintf_s(probeLine, "-- probe QI(0x%08X) => 0x%p\n", pid, p);
                        g_log += probeLine;
                        if (p == nullptr)
                            continue;
                        auto eg = (NvApiEnumGpus_t)p;
                        void* gpus[64] = {};
                        unsigned int cnt = 0;
                        int er = eg(gpus, &cnt);
                        char callLine[80];
                        sprintf_s(callLine, "   call => %d, count=%u\n", er, cnt);
                        g_log += callLine;
                        if (er != 0 || cnt == 0 || cnt > 64)
                            continue;
                        for (unsigned int g = 0; g < cnt; ++g) {
                            char name[64] = "<unknown>";
                            if (getName != nullptr)
                                getName(gpus[g], name);
                            char line2[256];
                            sprintf_s(line2, "   gpu#%u handle=0x%p name='%s'\n",
                                      g, gpus[g], name);
                            g_log += line2;
                            if (_strnicmp(name, "NVIDIA", 6) != 0)
                                continue;
                            triedAny = true;
                            int st = dyn(gpus[g], 1);
                            char line3[256];
                            sprintf_s(line3, st == 0
                                ? "   >> EnableDynamicPstates(gpu#%u, flag=1) => OK\n"
                                : "   >> EnableDynamicPstates(gpu#%u, flag=1) => %d (FAILED)\n",
                                g, st);
                            g_log += line3;
                            info.nvapi_status = st;
                            if (st == 0) {
                                FreeLibrary(hmod);
                                goto step2;
                            }
                        }
                    }
                    if (!triedAny)
                        g_log += "未通过 probes 拿到有效的 NVIDIA 物理GPU\n";
                }
            }
            // Only report an init-level failure when a real dyn() status wasn't
            // already recorded (rc is the NvAPI_Initialize result, 0 on success).
            if (rc != 0)
                info.nvapi_status = rc;
        }
        FreeLibrary(hmod);
    }

step2:
    g_log += (info.nvapi_status == 0)
        ? "NvAPI: 动态 P 状态已恢复\n"
        : "NvAPI 步骤失败\n";

    if (skip_clock_reset) {
        g_log += "已跳过时钟重置\n";
        out->nvapi_status = info.nvapi_status;
        out->clock_reset_ok = 0;
        out->smi_found = 0;
        return info.nvapi_status == 0 ? 1 : 0;
    }

    std::wstring smi = FindNvidiaSmi();
    if (smi.empty()) {
        g_log += "找不到 nvidia-smi，跳过时钟重置\n";
        out->nvapi_status = info.nvapi_status;
        out->clock_reset_ok = 0;
        out->smi_found = 0;
        return info.nvapi_status == 0 ? 1 : 0;
    }
    info.smi_found = 1;

    std::string stdoutText;
    bool timedOut = false;
    bool ok = RunCapture(smi, L"-rgc", 10000, stdoutText, timedOut);
    // RunCapture merges stderr into stdout; no separate stderr channel.
    size_t start = stdoutText.find_first_not_of(" \t\r\n");
    std::string trimmed = (start == std::string::npos) ? "" : stdoutText.substr(start);
    if (!trimmed.empty())
        g_log += std::string("-rgc: ") + trimmed + "\n";
    else
        g_log += "-rgc: 已执行（无输出）\n";

    info.clock_reset_ok = ok ? 1 : 0;
    out->nvapi_status = info.nvapi_status;
    out->clock_reset_ok = info.clock_reset_ok;
    out->smi_found = info.smi_found;
    return info.nvapi_status == 0 ? 1 : 0;
}

HONOR_API const char* hc_last_log() {
    std::lock_guard<std::mutex> lk(g_mutex);
    return g_log.c_str();
}

/* ---- GPU overclock (SetPstates20) ---- */

namespace {
// Cached SetPstates20 binding.
struct OcBinding {
    bool tried = false;
    bool ok = false;
    NvApiSetPstates20_t setPstates = nullptr;
    void* gpu = nullptr;
    std::string error;
};
OcBinding& Oc() {
    static OcBinding b;
    return b;
}
}  // namespace

// Internal helper: assumes g_mutex is already held. Probes NVAPI SetPstates20 once.
static int32_t OcEnsureLocked() {
    OcBinding& b = Oc();
    if (b.tried)
        return b.ok ? 1 : 0;
    b.tried = true;

    HMODULE hmod = LoadLibraryW(L"nvapi64.dll");
    if (hmod == nullptr) {
        b.error = "找不到 nvapi64.dll";
        return 0;
    }
    auto qifn = (NvApiQueryInterface_t)(void*)GetProcAddress(hmod, "nvapi_QueryInterface");
    if (qifn == nullptr) {
        b.error = "无 nvapi_QueryInterface 入口";
        FreeLibrary(hmod);
        return 0;
    }
    auto init = (NvApiInitialize_t)NvApiResolve(qifn, kQiInitialize);
    if (init == nullptr || init() != 0) {
        b.error = "NvAPI_Initialize 失败";
        FreeLibrary(hmod);
        return 0;
    }
    auto setP = (NvApiSetPstates20_t)NvApiResolve(qifn, kQiSetPstates20);
    auto eg = (NvApiEnumGpus_t)NvApiResolve(qifn, kQiEnumPhysicalGpus);
    if (setP == nullptr || eg == nullptr) {
        b.error = "SetPstates20/EnumGpus 未解析";
        FreeLibrary(hmod);
        return 0;
    }
    void* gpus[64] = {};
    unsigned int count = 0;
    if (eg(gpus, &count) != 0 || count == 0) {
        b.error = "EnumPhysicalGPUs 失败";
        FreeLibrary(hmod);
        return 0;
    }
    b.setPstates = setP;
    b.gpu = gpus[0];
    b.ok = true;
    b.error.clear();
    return 1;
}

HONOR_API int32_t hc_oc_available() {
    std::lock_guard<std::mutex> lk(g_mutex);
    return OcEnsureLocked();
}

HONOR_API const char* hc_oc_last_error() {
    std::lock_guard<std::mutex> lk(g_mutex);
    return Oc().error.c_str();
}

HONOR_API int32_t hc_oc_apply_offset(int32_t clock_domain, int32_t offset_mhz) {
    std::lock_guard<std::mutex> lk(g_mutex);
    // Use the locked variant; calling hc_oc_available() here would re-lock g_mutex
    // (a non-recursive mutex) and fail-fast the process.
    if (OcEnsureLocked() != 1)
        return -1;
    OcBinding& b = Oc();
    if (b.setPstates == nullptr)
        return -2;

    // NV_GPU_PERF_PSTATES20_INFO_V2 layout (see prior managed code):
    const int kInfoSize = 0x1C94;
    const uint32_t kInfoVersion = 0x11C94;
    std::vector<uint8_t> raw(kInfoSize, 0);
    auto writeU32 = [&](int off, uint32_t v) {
        raw[off] = (uint8_t)(v & 0xFF);
        raw[off + 1] = (uint8_t)((v >> 8) & 0xFF);
        raw[off + 2] = (uint8_t)((v >> 16) & 0xFF);
        raw[off + 3] = (uint8_t)((v >> 24) & 0xFF);
    };
    writeU32(0x00, kInfoVersion);
    writeU32(0x08, 1);
    writeU32(0x0C, 1);
    writeU32(0x10, 0);
    writeU32(0x14, 0);
    writeU32(0x1C, (uint32_t)clock_domain);
    writeU32(0x28, (uint32_t)(offset_mhz * 1000));   // freqDelta kHz

    return b.setPstates(b.gpu, raw.data());
}

}  // extern "C"
