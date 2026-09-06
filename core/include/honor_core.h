#pragma once
/*
 * honor_core.h — C ABI for the native honor-helper core library.
 *
 * This library owns all hardware control for HONOR WIN H7 laptops:
 *   - HONOR WMI (ROOT\WMI\OemWMIMethod): perf mode, PPM, temperatures,
 *     fan speed, touchpad (admin required).
 *   - Windows perf counters (cpu freq).
 *   - NVIDIA NVAPI (nvapi64.dll): GPU stats, dynamic-Pstate unlock, SetPstates20 OC.
 *   - nvidia-smi fallback + clock reset.
 *
 * The C# (WinUI) front-end P/Invokes into these functions; all functions are
 * thread-safe and may be called from any thread the managed app uses. Return
 * conventions:
 *   - int32_t results that are states return the value or -1 on failure.
 *   - int32_t predicates return 1 on success/true, 0 on failure/false.
 *   - pointers out-params are filled only when the function succeeds.
 */
#include <stdint.h>

#if defined(_WIN32) && defined(HONOR_CORE_EXPORTS)
#  define HONOR_API __declspec(dllexport)
#elif defined(_WIN32)
#  define HONOR_API __declspec(dllimport)
#else
#  define HONOR_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t hc_status; /* 0 = ok, negative = error */

/* ---- lifecycle ---- */

/* Initialize COM + caches. Returns 0 on success (safe to call repeatedly). */
HONOR_API hc_status hc_init(void);

/* Release cached COM objects. Should be called once at process exit. */
HONOR_API void hc_shutdown(void);

/* Whether the current process token is elevated (admin). 1 = yes, 0 = no. */
HONOR_API int32_t hc_is_admin(void);

/* ---- performance mode / WMI (admin) ---- */

/* Current HONOR perf mode (0..4), or -1 on failure. */
HONOR_API int32_t hc_get_perf_mode(void);

/* Switch perf mode. Returns 1 on success, 0 on failure. */
HONOR_API int32_t hc_set_perf_mode(int32_t mode);

/* Set PPM level (0..4). Returns 1 on success, 0 on failure.
   PPM is mode-gated (only beast), so a direct failure auto-switches to beast. */
HONOR_API int32_t hc_set_ppm(int32_t level);

/* Read one temperature channel (°C), or -1 on failure. */
HONOR_API int32_t hc_get_temp(int32_t channel);

/* Read fan speed (rpm) for channel id; out_limit receives the optional
   target/limit rpm (or -1). Returns rpm, or -1 on failure. */
HONOR_API int32_t hc_get_fan_speed(int32_t id, int32_t* out_limit);

/* Touchpad state: 1 = on, 0 = off, -1 = read failure. */
HONOR_API int32_t hc_get_touchpad_state(void);

/* Set touchpad on/off. Returns 1 on success, 0 on failure. */
HONOR_API int32_t hc_set_touchpad_state(int32_t on);

/* CPU average frequency (MHz), or -1 on failure. No admin required. */
HONOR_API int32_t hc_get_cpu_freq_mhz(void);

/* ---- GPU status (NVAPI, falls back to nvidia-smi) ---- */

typedef struct hc_gpu_stats {
    int32_t clock_mhz; /* core clock (MHz), -1 if unknown */
    int32_t util_pct;  /* 3D utilization %, -1 if unknown */
} hc_gpu_stats;

/* Returns 1 on success, 0 on failure. */
HONOR_API int32_t hc_gpu_stats_query(hc_gpu_stats* out);

/* ---- GPU clock unlock (NvAPI EnableDynamicPstates + nvidia-smi -rgc) ---- */

typedef struct hc_gpu_fix {
    int32_t nvapi_status;   /* 0 = OK, else NVAPI status code */
    int32_t clock_reset_ok; /* 1 = nvidia-smi -rgc executed, 0 = skipped/failed */
    int32_t smi_found;      /* 1 = nvidia-smi was located */
} hc_gpu_fix;

/* Runs the whole unlock. Returns 1 if the NvAPI step succeeded, 0 otherwise. */
HONOR_API int32_t hc_gpu_clockfix_run(int32_t skip_clock_reset, hc_gpu_fix* out);

/* UTF-8 diagnostic log from the last gpu-clockfix run. Valid until the next
   call into this library. Returns empty string if none. */
HONOR_API const char* hc_last_log(void);

/* ---- GPU overclock (NvAPI SetPstates20) ---- */

#define HC_CLOCK_GRAPHICS 0
#define HC_CLOCK_MEMORY   4

/* Returns 1 if SetPstates20 is available, 0 otherwise. */
HONOR_API int32_t hc_oc_available(void);

/* UTF-8 description of the last OC probe failure (null if none). */
HONOR_API const char* hc_oc_last_error(void);

/* Apply a clock offset (MHz, may be negative) to clock_domain.
   Returns NVAPI status code: 0 = success, -1 = NVAPI init failed,
   -2 = SetPstates20 not resolved. */
HONOR_API int32_t hc_oc_apply_offset(int32_t clock_domain, int32_t offset_mhz);

#ifdef __cplusplus
}
#endif
