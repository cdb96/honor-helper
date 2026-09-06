using System.Collections.Generic;

namespace HonorHelper;

/// <summary>
/// Shared, hardware-agnostic channel/profile tables used by both the service (to
/// read hardware) and the UI (to render labels and grids). These are pure data —
/// no native calls, no hardware access — so they live in the common library and
/// are identical on both sides of the pipe.
/// </summary>
public static class CoreTables
{
    /// <summary>Perf mode that PPM is gated behind (only beast accepts it).</summary>
    public const int BeastPerfMode = 3;

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

    public static readonly (int Id, string Name)[] Fans =
    {
        (0, "CPU"),
        (1, "GPU"),
        (2, "系统"),
    };

    /// <summary>Display names for the low-level PPM level (0-4).</summary>
    public static readonly IReadOnlyDictionary<int, string> PpmNames = new Dictionary<int, string>
    {
        [0] = "Balanced",
        [1] = "Level 1",
        [2] = "Level 2",
        [3] = "Beast",
        [4] = "Level 4 max",
    };

    /// <summary>Display names for the underlying perf mode (0-4).</summary>
    public static readonly IReadOnlyDictionary<int, string> PerfModeNames = new Dictionary<int, string>
    {
        [0] = "智能 Balanced",
        [1] = "高能 High Energy",
        [2] = "Level 2",
        [3] = "狂战 Beast",
        [4] = "Level 4 max",
    };

    /// <summary>The three profiles shown in the UI: 智能 / 高能 / 狂战.</summary>
    public static readonly PerfProfile[] Profiles =
    {
        new PerfProfile("smart", "智能", "Smart / Balanced", 0),
        new PerfProfile("high",  "高能", "High Energy",      1),
        new PerfProfile("beast", "狂战", "Rage / Beast",     3),
    };
}
