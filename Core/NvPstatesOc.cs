using System;
using System.Runtime.InteropServices;

namespace HonorHelper;

public static class NvPstatesOc
{
    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _ok;
    private static IntPtr _gpu;
    private static QueryInterfaceDelegate? _qi;
    private static SetPstates20Delegate? _setPstates;

    // NVAPI 函数 ID（见 NVAPI_CALL_USAGE.md §6）
    private const uint IdEnumGpus = 0xE5AC921F;
    private const uint IdSetPstates20 = 0x0F4DAE6B;

    // 时钟域 ID（NVAPI_CALL_USAGE.md §4 + HNSDK 反编译：显存域 = 4，标准 NVAPI 的 1 会被驱动拒绝）
    public const uint ClockGraphics = 0;
    public const uint ClockMemory = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint interfaceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumGpusDelegate([Out] IntPtr[] physGpus, out uint gpuCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetPstates20Delegate(IntPtr physicalGpu, IntPtr pset);

    /// <summary>NVAPI SetPstates20 是否可用（首次调用探测并缓存）。</summary>
    public static bool IsAvailable()
    {
        lock (Gate)
        {
            Ensure();
            return _ok;
        }
    }

    /// <summary>最近一次探测失败的原因（null = 成功），用于状态栏诊断。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 应用频率偏移（MHz，可负）。clockDomain：0=核心，1=显存。
    /// 返回 NVAPI 状态码：0=成功；-1=NVAPI 初始化失败；-2=SetPstates20 未解析到。
    /// 结构按 nvapi.h NV_GPU_PERF_PSTATES20_INFO_V1 显式偏移写入（详见 BuildPstates20）。
    /// </summary>
    public static int ApplyOffset(uint clockDomain, int offsetMHz)
    {
        lock (Gate)
        {
            if (!Ensure())
                return -1;
            if (_setPstates is null)
                return -2;

            byte[] raw = BuildPstates20(clockDomain, offsetMHz);
            var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                return _setPstates(_gpu, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }

    // ---------- NV_GPU_PERF_PSTATES20_INFO_V2 显式布局（实测驱动 616.56 接受） ----------
    // version = 0x11C94（size 0x1C94=7316 | ver2<<16）
    // 头部：0x00 version；0x08 numPstates；0x0C numClocks；0x10 numBaseVoltages
    // pstates[0] @ 0x14：pstateId @ 0x14；flags @ 0x18；
    // clocks[0] @ 0x1C：domainId @ 0x1C；flags @ 0x20；freqDelta.value @ 0x28（kHz）
    private const int InfoSize = 0x1C94;
    private const uint InfoVersion = 0x11C94;

    private static byte[] BuildPstates20(uint clockDomain, int offsetMHz)
    {
        var b = new byte[InfoSize];
        WriteU32(b, 0x00, InfoVersion);
        WriteU32(b, 0x08, 1);                              // numPstates
        WriteU32(b, 0x0C, 1);                              // numClocks
        WriteU32(b, 0x10, 0);                              // numBaseVoltages
        WriteU32(b, 0x14, 0);                              // pstates[0].pstateId = 0
        WriteU32(b, 0x1C, clockDomain);                    // clocks[0].domainId
        WriteU32(b, 0x28, (uint)(offsetMHz * 1000));       // freqDelta (kHz)
        return b;
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    private static bool Ensure()
    {
        if (_tried)
            return _ok;
        _tried = true;
        try
        {
            var hmod = LoadLibrary("nvapi64.dll");
            if (hmod == IntPtr.Zero)
            {
                LastError = "找不到 nvapi64.dll";
                return false;
            }

            var qiptr = GetProcAddress(hmod, "nvapi_QueryInterface");
            if (qiptr == IntPtr.Zero)
            {
                LastError = "无 nvapi_QueryInterface 入口";
                return false;
            }
            _qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qiptr);

            var initPtr = _qi(0x0150E828);   // NvAPI_Initialize
            if (initPtr == IntPtr.Zero)
            {
                LastError = "QI(NvAPI_Initialize) 为空";
                return false;
            }
            int initRc = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr)();
            if (initRc != 0)
            {
                LastError = $"NvAPI_Initialize rc={initRc}";
                return false;
            }

            var setPtr = _qi(IdSetPstates20);   // NvAPI_GPU_SetPstates20
            if (setPtr == IntPtr.Zero)
            {
                LastError = "QI(0x0F4DAE6B SetPstates20) 为空";
                return false;
            }
            _setPstates = Marshal.GetDelegateForFunctionPointer<SetPstates20Delegate>(setPtr);

            var enumPtr = _qi(IdEnumGpus);   // NvAPI_EnumPhysicalGPUs
            if (enumPtr == IntPtr.Zero)
            {
                LastError = "QI(EnumPhysicalGPUs) 为空";
                return false;
            }
            var eg = Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(enumPtr);
            var gpus = new IntPtr[64];
            int egRc = eg(gpus, out uint count);
            if (egRc != 0 || count == 0)
            {
                LastError = $"EnumPhysicalGPUs rc={egRc} count={count}";
                return false;
            }

            _gpu = gpus[0];   // 本机单 NVIDIA GPU
            LastError = null;
            _ok = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "初始化异常: " + ex.Message;
            return false;
        }
    }

    // （结构布局已改为 BuildPstates20 的显式偏移字节写入，不再用 marshal 推断）
}
