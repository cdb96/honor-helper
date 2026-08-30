using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace HonorHelper;

/// <summary>
/// Unlock GPU clocks pinned by HONOR Manager's "game scene" (移植自 Enable-DynamicPstates.ps1):
///   1) NvAPI_GPU_EnableDynamicPstates(1) via dynamic nvapi64.dll binding
///   2) nvidia-smi -rgc to reset driver-level clock lock
/// </summary>
public static class GpuClockFix
{
    public sealed record Result(bool Ok, string Message);

    public sealed record GpuStats(int ClockMhz, int UtilPercent);

    /// <summary>
    /// 查询当前 GPU 频率与占用率：优先 NvAPI 进程内查询（微秒级，无进程拉起），
    /// NvAPI 不可用时回退 nvidia-smi 进程。失败返回 null。
    /// </summary>
    public static Task<GpuStats?> GetGpuStatsAsync()
        => Task.Run(() => NvStats.Query() ?? QueryViaSmi());

    private static GpuStats? QueryViaSmi()
    {
        var smi = FindNvidiaSmi();
        if (smi is null)
            return null;
        try
        {
            if (!RunNvidiaSmi(smi,
                "--query-gpu=clocks.current.graphics,utilization.gpu --format=csv,noheader,nounits",
                5_000, out var stdout, out _))
                return null;

            // 输出形如 "2100, 65"
            var parts = stdout.Trim().Split(',');
            if (parts is { Length: >= 2 } &&
                int.TryParse(parts[0].Trim(), out var mhz) &&
                int.TryParse(parts[1].Trim(), out var util))
                return new GpuStats(mhz, util);
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- public entry ----------

    /// <summary>Run both steps off the UI thread. Returns a user-facing message.</summary>
    public static Task<Result> RunAsync(bool skipClockReset = false)
        => Task.Run(() => Run(skipClockReset));

    public static Result Run(bool skipClockReset = false)
    {
        int rc = -999;
        try
        {
            rc = NvApi.Run(1, 0);
        }
        catch (Exception ex)
        {
            NvApi.Log.AppendLine($"NvAPI 异常: {ex.Message}");
        }

        var msg = new StringBuilder();
        msg.Append(NvApi.Log);

        if (rc == 0)
            msg.AppendLine("NvAPI: 动态 P 状态已恢复");
        else
            msg.AppendLine($"NvAPI 步骤失败 (code {rc})");

        // ---------- step 2: nvidia-smi -rgc ----------
        if (skipClockReset)
        {
            msg.AppendLine("已跳过时钟重置");
            return new Result(rc == 0, msg.ToString());
        }

        var smi = FindNvidiaSmi();
        if (smi is null)
        {
            msg.AppendLine("找不到 nvidia-smi，跳过时钟重置");
            return new Result(rc == 0, msg.ToString());
        }

        try
        {
            RunNvidiaSmi(smi, "-rgc", 10_000, out var stdout, out var stderr);
            var outLine = (stdout + " " + stderr).Trim();
            if (outLine.Length > 0)
                msg.AppendLine($"-rgc: {outLine}");
            else
                msg.AppendLine("-rgc: 已执行（无输出）");
        }
        catch (Exception ex)
        {
            msg.AppendLine($"-rgc 执行失败: {ex.Message}");
        }

        return new Result(rc == 0, msg.ToString());
    }

    private static bool RunNvidiaSmi(string fileName, string arguments, int timeoutMs,
        out string stdout, out string stderr)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (p is null)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            return false;
        }

        // Drain both redirected streams concurrently so a full pipe cannot deadlock the child.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit();
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            return false;
        }

        stdout = stdoutTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();
        return p.ExitCode == 0;
    }

    private static string? FindNvidiaSmi()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        })
        {
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    // ---------- dynamic NvAPI binding (same as the PS Add-Type block) ----------

    /// <summary>
    /// NvAPI 进程内 GPU 状态查询：GetAllClocks 的 PROCESSOR 域取核心频率（kHz→MHz），
    /// GetUsages[2] 取 3D 占用率。接口 ID 与动态绑定方式同 NvApi；
    /// 初始化失败（无 NVIDIA 驱动等）会记住并直接走 nvidia-smi 兜底。
    /// </summary>
    private static class NvStats
    {
        private static readonly object Gate = new();
        private static bool _failed;
        private static QueryInterfaceDelegate? _qi;
        private static GetUsagesDelegate? _getUsages;
        private static GetAllClocksDelegate? _getAllClocks;
        private static IntPtr _gpu = IntPtr.Zero;
        private static readonly uint[] UsageEntries = new uint[33];
        private static readonly uint[] ClockEntries = new uint[64];

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
        private delegate int GetUsagesDelegate(IntPtr physicalGpu, ref NvUsages usages);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAllClocksDelegate(IntPtr physicalGpu, ref NvClocks clocks);

        [StructLayout(LayoutKind.Sequential)]
        private struct NvUsages
        {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
            public uint[] Entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvClocks
        {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public uint[] Clocks;
        }

        private static T? Qi<T>(uint id) where T : Delegate
        {
            var ptr = _qi!(id);
            return ptr == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        public static GpuStats? Query()
        {
            lock (Gate)
            {
                try
                {
                    if (_failed)
                        return null;
                    if (_qi is null && !Init())
                        return null;

                    if (_getUsages is null || _getAllClocks is null)
                    {
                        _failed = true;
                        return null;
                    }

                    Array.Clear(UsageEntries);
                    Array.Clear(ClockEntries);
                    var usages = new NvUsages
                    {
                        Version = (uint)Marshal.SizeOf<NvUsages>() | (1u << 16),
                        Entries = UsageEntries,
                    };
                    var clocks = new NvClocks
                    {
                        Version = (uint)Marshal.SizeOf<NvClocks>() | (1u << 16),
                        Clocks = ClockEntries,
                    };

                    if (_getUsages(_gpu, ref usages) != 0 || _getAllClocks(_gpu, ref clocks) != 0)
                        return null;

                    int util = usages.Entries.Length > 2 ? (int)usages.Entries[2] : 0;
                    int mhz = (int)(clocks.Clocks[0] / 1000u);   // CLOCK_DOMAIN_PROCESSOR(0)，单位 kHz
                    return new GpuStats(mhz, Math.Clamp(util, 0, 100));
                }
                catch
                {
                    _failed = true;   // 结构性失败：后续轮询直接走 nvidia-smi 兜底
                    return null;
                }
            }
        }

        private static bool Init()
        {
            var hmod = LoadLibrary("nvapi64.dll");
            if (hmod == IntPtr.Zero) { _failed = true; return false; }

            var qiptr = GetProcAddress(hmod, "nvapi_QueryInterface");
            if (qiptr == IntPtr.Zero) { _failed = true; return false; }
            _qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qiptr);

            var initPtr = _qi(0x0150E828);   // NvAPI_Initialize
            if (initPtr == IntPtr.Zero) { _failed = true; return false; }
            if (Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr)() != 0)
            { _failed = true; return false; }

            var enumPtr = _qi(0xE5AC921F);   // NvAPI_EnumPhysicalGPUs
            if (enumPtr == IntPtr.Zero) { _failed = true; return false; }
            var eg = Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(enumPtr);
            var gpus = new IntPtr[64];
            if (eg(gpus, out uint count) != 0 || count == 0)
            { _failed = true; return false; }

            _gpu = gpus[0];   // 本机单 GPU，取第一块物理 GPU
            _getUsages = Qi<GetUsagesDelegate>(0x189A1FDF);
            _getAllClocks = Qi<GetAllClocksDelegate>(0x1BD69F49);
            if (_getUsages is null || _getAllClocks is null)
            {
                _failed = true;
                return false;
            }
            return true;
        }
    }

    // ---------- dynamic NvAPI binding (same as the PS Add-Type block) ----------

    private static class NvApi
    {
        public static readonly StringBuilder Log = new();

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
        private delegate int EnableDynamicPstatesDelegate(IntPtr physicalGpu, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFullNameDelegate(IntPtr physicalGpu, [MarshalAs(UnmanagedType.LPStr)] StringBuilder name);

        public static int Run(uint flag, uint probeId)
        {
            Log.Clear();

            var hmod = LoadLibrary("nvapi64.dll");
            if (hmod == IntPtr.Zero)
            {
                Log.AppendLine("LoadLibrary(nvapi64.dll) 失败（无 NVIDIA 驱动？）");
                return -100;
            }

            var qiptr = GetProcAddress(hmod, "nvapi_QueryInterface");
            var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qiptr);

            var initPtr = qi(0x0150E828);                       // NvAPI_Initialize
            if (initPtr == IntPtr.Zero)
            {
                Log.AppendLine("QI Init null");
                return -103;
            }
            var r = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr)();
            Log.AppendLine($"NvAPI_Initialize => {r}");
            if (r != 0)
                return r;

            var dynPtr = qi(0xFA579A0F);                        // NvAPI_GPU_EnableDynamicPstates
            Log.AppendLine($"QI(0xFA579A0F EnableDynamicPstates) => 0x{dynPtr:X}");
            if (dynPtr == IntPtr.Zero)
                return -102;
            var dyn = Marshal.GetDelegateForFunctionPointer<EnableDynamicPstatesDelegate>(dynPtr);

            var fnPtr = qi(0xCEEE8E9F);                         // NvAPI_GPU_GetFullName
            GetFullNameDelegate? getName = null;
            if (fnPtr != IntPtr.Zero)
                getName = Marshal.GetDelegateForFunctionPointer<GetFullNameDelegate>(fnPtr);

            // 0xE5AC921F = NvAPI_EnumPhysicalGPUs（新驱动可用）；其余为旧接口后备
            uint[] probes = probeId != 0
                ? new[] { probeId }
                : new[] { 0xE5AC921Fu, 0x33C7358Cu, 0xAD298D3Fu, 0xD22BDD7Eu };

            int status = -200;
            bool triedAny = false;

            foreach (var id in probes)
            {
                var p = qi(id);
                Log.AppendLine($"-- probe QI(0x{id:X}) => 0x{p:X}");
                if (p == IntPtr.Zero)
                    continue;

                EnumGpusDelegate eg;
                try
                {
                    eg = Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(p);
                }
                catch (Exception ex)
                {
                    Log.AppendLine($"marshal 失败: {ex.Message}");
                    continue;
                }

                var gs = new IntPtr[64];
                uint cnt;
                int er;
                try
                {
                    er = eg(gs, out cnt);
                }
                catch (Exception ex)
                {
                    Log.AppendLine($"调用抛异常: {ex.Message}");
                    continue;
                }

                Log.AppendLine($"   call => {er}, count={cnt}");
                if (er != 0 || cnt == 0 || cnt > 64)
                    continue;

                for (int g = 0; g < cnt; g++)
                {
                    var name = "<unknown>";
                    if (getName is not null)
                    {
                        try
                        {
                            var sb = new StringBuilder(64);
                            getName(gs[g], sb);
                            name = sb.ToString();
                        }
                        catch
                        {
                            // name is best-effort
                        }
                    }

                    Log.AppendLine($"   gpu#{g} handle=0x{gs[g]:X} name='{name}'");
                    if (!name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        continue;

                    triedAny = true;
                    status = dyn(gs[g], flag);
                    Log.AppendLine(status == 0
                        ? $"   >> EnableDynamicPstates(gpu#{g}, flag={flag}) => OK"
                        : $"   >> EnableDynamicPstates(gpu#{g}, flag={flag}) => {status} (FAILED)");
                    if (status == 0)
                        return 0;
                }
            }

            if (!triedAny)
                Log.AppendLine("未通过 probes 拿到有效的 NVIDIA 物理GPU");
            return status;
        }
    }
}
