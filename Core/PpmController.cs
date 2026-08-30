using System;
using System.Linq;
using System.Threading.Tasks;
using WmiLight;

namespace HonorHelper;

/// <summary>
/// Talks to HONOR's WMI interface (ROOT\WMI\OemWMIMethod) to switch the
/// performance (perf) mode and the PPM level. Mirrors the logic in Set-SPPM.ps1.
/// Requires Administrator.
///
/// Uses WmiLight (AOT-compatible) instead of System.Management so the app can be
/// published with NativeAOT. WmiLight drives the connect/query and the in-parameters
/// object; the actual OemWMIfun call is made through the small native bridge in
/// <see cref="WmiNative"/> because WmiLight's public API neither writes a byte[]
/// input nor handles this instance method via its high-level ExecuteMethod.
///
/// All calls are serialized through one lock and cached so repeated polling does not
/// reconnect/re-enumerate (which used to freeze the UI thread). The UI awaits the
/// *Async wrappers, which run the blocking WMI work on a background thread.
/// </summary>
public sealed class PpmController : IDisposable
{
    private const string WmiNamespace = @"\\.\ROOT\WMI";
    private const string ClassName = "OemWMIMethod";
    private const string MethodName = "OemWMIfun";

    /// <summary>Perf mode that PPM is gated behind (only beast accepts it).</summary>
    private const int BeastPerfMode = 3;

    public sealed record Result(string Message, bool Success);

    private readonly object _wmiLock = new();
    private WmiConnection? _conn;
    private string? _instancePath;

    private WmiConnection Connection
    {
        get
        {
            _conn ??= new WmiConnection(WmiNamespace);
            return _conn;
        }
    }

    /// <summary>Find the first HWMI instance path (lazy, cached). Throws if not found.</summary>
    private string GetInstancePath()
    {
        if (_instancePath is not null)
            return _instancePath;

        var conn = Connection;
        foreach (var o in conn.CreateQuery($"SELECT * FROM {ClassName}"))
        {
            try
            {
                var name = o.GetPropertyValue<string>("InstanceName") ?? string.Empty;
                if (name.Contains("HWMI", StringComparison.OrdinalIgnoreCase))
                {
                    _instancePath = o.Path;
                    return _instancePath;
                }
            }
            finally
            {
                o.Dispose();
            }
        }

        throw new InvalidOperationException($"{ClassName} not found. Run as Administrator.");
    }

    private byte[] Invoke(byte[] command)
    {
        lock (_wmiLock)
        {
            // one retry: if the cached instance went stale (WMI service cycled,
            // mode switch invalidated it), drop it and rebuild once.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return WmiNative.Invoke(Connection, GetInstancePath(), command);
                }
                catch (Exception ex) when (attempt == 0 && IsRecoverable(ex))
                {
                    ResetConnection();
                }
            }
        }
    }

    private static bool IsRecoverable(Exception ex)
        => ex is System.Runtime.InteropServices.COMException
           or System.IO.IOException;

    private void ResetConnection()
    {
        _conn?.Dispose();
        _conn = null;
        _instancePath = null;
    }

    /// <summary>Query current perf mode. Returns -1 on any failure.</summary>
    public int GetCurrentPerfMode()
    {
        try
        {
            var o = Invoke(new byte[] { 0x04, 0x0E });
            if (o.Length < 2)
                return -1;
            // o[0]==0 is the normal success status; 0xEE also appears on some
            // firmware while o[1] still carries a valid mode byte.
            if (o[0] != 0 && o[0] != 0xEE)
                return -1;
            return o[1];
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Switch the perf mode (04 0F &lt;mode&gt;).</summary>
    public bool SetPerfMode(int mode)
    {
        var o = Invoke(new byte[] { 0x04, 0x0F, (byte)mode });
        return o.Length >= 1 && o[0] == 0;
    }

    // ---------- temperature (02 02 <channel>, from Get-HonorTemps.ps1) ----------

    /// <summary>实测有效的温度通道（详见 Get-HonorTemps.ps1）。</summary>
    public sealed record TempChannel(int Channel, string Name);

    public static readonly IReadOnlyList<TempChannel> TempChannels = new[]
    {
        new TempChannel(0x00, "CPU"),
        new TempChannel(0x01, "GPU"),
        new TempChannel(0x16, "风扇"),
        new TempChannel(0x0B, "内存"),
        new TempChannel(0x2B, "EC"),
        new TempChannel(0x05, "芯片组"),
        new TempChannel(0x08, "充电区"),
        new TempChannel(0x0E, "电池"),
        new TempChannel(0x15, "DC口"),
        new TempChannel(0x0F, "主板"),
        new TempChannel(0x2D, "CPU供电"),
        new TempChannel(0x2C, "GPU供电"),
    };

    /// <summary>
    /// Read one temperature channel (02 02 &lt;ch&gt;). Returns °C, or null on failure.
    /// </summary>
    public int? GetTemp(int channel)
    {
        try
        {
            var o = Invoke(new byte[] { 0x02, 0x02, (byte)channel });
            if (o.Length > 2 && o[0] == 0)
                return o[2];
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read all channels off the UI thread. Returns name -> °C (失败通道不含在内)。</summary>
    public Task<Dictionary<string, int>> GetTempsAsync()
        => Task.Run(() =>
        {
            var dict = new Dictionary<string, int>();
            foreach (var c in TempChannels)
            {
                var t = GetTemp(c.Channel);
                if (t.HasValue)
                    dict[c.Name] = t.Value;
            }
            return dict;
        });

    // ---------- fan speed (02 08 <id>，HNSDK_GetFanSpeed) ----------

    /// <summary>
    /// 风扇通道与显示名。实测 EC 共暴露 3 个通道（id 0-2 命令 status=0；id 3+ 返回 0x01 无效）：
    /// 0=CPU、1=GPU、2=系统。id 2 当前读数 0，疑似停转/零转速模式。改 Name 即可换显示名。
    /// </summary>
    public static readonly (int Id, string Name)[] Fans =
    {
        (0, "CPU"),
        (1, "GPU"),
        (2, "系统"),
    };

    /// <summary>
    /// 风扇读数：Rpm 为 out[1..2] 的 u16（0 = 停转）；
    /// Limit 是 out[3..4] 的第二个 u16，实测略高于当前转速，疑为目标/上限转速（未证实，可能为 null）。
    /// </summary>
    public sealed record FanSpeed(int Rpm, int? Limit);

    /// <summary>转速合理上限，超出视为无效读数（正常风扇远低于此值）。</summary>
    private const int MaxPlausibleRpm = 15000;

    /// <summary>解析 @off 处的 u16：按小端读，不合理时按大端兜底；0 合法（停转）。</summary>
    private static int? DecodeRpm(byte[] o, int off)
    {
        if (o.Length < off + 2)
            return null;
        int le = o[off] | (o[off + 1] << 8);
        if (le <= MaxPlausibleRpm)
            return le;
        int be = (o[off] << 8) | o[off + 1];
        return be <= MaxPlausibleRpm ? be : null;
    }

    /// <summary>读风扇转速（02 08 &lt;id&gt;）。返回 null 表示通道无效或读取失败。</summary>
    public FanSpeed? GetFanSpeed(int id)
    {
        try
        {
            var o = Invoke(new byte[] { 0x02, 0x08, (byte)id });
            if (o.Length < 3 || o[0] != 0)
                return null;
            var rpm = DecodeRpm(o, 1);
            if (rpm is null)
                return null;
            return new FanSpeed(rpm.Value, DecodeRpm(o, 3));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读全部风扇（后台线程）。失败的风扇不在字典里。</summary>
    public Task<Dictionary<int, FanSpeed>> GetFanSpeedsAsync()
        => Task.Run(() =>
        {
            var dict = new Dictionary<int, FanSpeed>();
            foreach (var (id, _) in Fans)
            {
                var f = GetFanSpeed(id);
                if (f is not null)
                    dict[id] = f;
            }
            return dict;
        });

    // ---------- touchpad（02 0F FF 读 / 02 10 <0|1> 写，见 Set-Touchpad.ps1） ----------

    /// <summary>读触控板开关（02 0F FF）。1=开，0=关，null=读取失败。</summary>
    public int? GetTouchpadState()
    {
        try
        {
            var o = Invoke(new byte[] { 0x02, 0x0F, 0xFF });
            if (o.Length < 2 || o[0] != 0)
                return null;
            if (o[1] != 0 && o[1] != 1)
                return null;
            return o[1];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>设置触控板开关（02 10 &lt;0|1&gt;）。</summary>
    public bool SetTouchpadState(bool on)
    {
        var o = Invoke(new byte[] { 0x02, 0x10, (byte)(on ? 1 : 0) });
        return o.Length >= 1 && o[0] == 0;
    }

    public Task<int?> GetTouchpadStateAsync() => Task.Run(GetTouchpadState);

    public Task<bool> SetTouchpadStateAsync(bool on) => Task.Run(() => SetTouchpadState(on));

    /// <summary>
    /// 当前 CPU 平均频率（MHz）= 基准频率 × 性能百分比
    /// （「Processor Information」性能计数器，无需管理员）。
    /// 该计数器在 root\cimv2 命名空间，与 HONOR 接口的 \\.\ROOT\WMI 不同，
    /// 所以这里新建一个独立的 cimv2 连接（WmiLight）。
    /// </summary>
    public Task<int?> GetCpuFreqMhzAsync()
        => Task.Run(() =>
        {
            try
            {
                using var cimv2 = new WmiConnection(@"\\.\ROOT\cimv2");
                foreach (var o in cimv2.CreateQuery(
                    "SELECT PercentProcessorPerformance, ProcessorFrequency " +
                    "FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'"))
                {
                    try
                    {
                        // 用 Convert.ToDouble 兼容 UInt64/UInt32 等不同数值类型。
                        var perf = Convert.ToDouble(o.GetPropertyValue("PercentProcessorPerformance"));
                        var baseMhz = Convert.ToDouble(o.GetPropertyValue("ProcessorFrequency"));
                        if (baseMhz > 0)
                            return (int?)Math.Round(baseMhz * perf / 100.0);
                    }
                    finally
                    {
                        o.Dispose();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        });

    /// <summary>
    /// Set PPM level (07 0D &lt;level&gt;). PPM is mode-gated: it is rejected outside the
    /// beast perf mode, so if a direct write fails we auto-switch to beast and retry.
    /// </summary>
    public bool SetPpm(int level)
    {
        var levelB = (byte)level;

        // 1. try direct
        var o = Invoke(new byte[] { 0x07, 0x0D, levelB });
        if (o.Length >= 1 && o[0] == 0)
            return true;

        // 2. gated -> switch to beast then retry
        if (!SetPerfMode(BeastPerfMode))
            return false;
        System.Threading.Thread.Sleep(300);

        o = Invoke(new byte[] { 0x07, 0x0D, levelB });
        return o.Length >= 1 && o[0] == 0;
    }

    /// <summary>Apply a whole profile (perf mode + optional PPM).</summary>
    public Result ApplyProfile(PerfProfile profile, int? ppm)
    {
        try
        {
            if (SetPerfMode(profile.PerfMode))
            {
                var msg = $"已切换：{profile.Display}（perf {profile.PerfMode}）";
                // PPM only makes sense in beast mode.
                if (ppm.HasValue && profile.PerfMode == BeastPerfMode)
                {
                    if (SetPpm(ppm.Value))
                        msg += $" · PPM = {ppm.Value}";
                    else
                        msg += " · PPM 设置失败";
                }
                return new Result(msg, true);
            }

            // If setting the profile's perf mode failed (e.g. unknown mode), try beast + PPM.
            if (profile.PerfMode != BeastPerfMode && SetPerfMode(BeastPerfMode))
            {
                var msg = $"已切换：狂战 (Beast)（目标 {profile.Display} 不可用）";
                if (ppm.HasValue && SetPpm(ppm.Value))
                    msg += $" · PPM = {ppm.Value}";
                return new Result(msg, true);
            }

            return new Result("切换失败：无法写入 HONOR WMI。请以管理员身份运行。", false);
        }
        catch (Exception ex)
        {
            return new Result($"错误：{ex.Message}", false);
        }
    }

    public bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ---------- async wrappers (run the blocking WMI work off the UI thread) ----------

    public Task<int> GetCurrentPerfModeAsync() => Task.Run(GetCurrentPerfMode);

    public Task<bool> SetPerfModeAsync(int mode) => Task.Run(() => SetPerfMode(mode));

    public Task<bool> SetPpmAsync(int level) => Task.Run(() => SetPpm(level));

    public Task<Result> ApplyProfileAsync(PerfProfile profile, int? ppm)
        => Task.Run(() => ApplyProfile(profile, ppm));

    public void Dispose()
    {
        lock (_wmiLock)
        {
            ResetConnection();
        }
    }
}
