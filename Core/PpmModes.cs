using System.Collections.Generic;

namespace HonorHelper;

/// <summary>
/// A named user-facing performance profile. Each profile maps to an underlying
/// HONOR "perf mode" (WMI 04 0F &lt;mode&gt;). The three profiles are 智能 / 高能 / 狂战.
/// </summary>
public sealed record PerfProfile(
    string Id,       // stable id used by the UI
    string Name,     // Chinese display name
    string SubName,  // English / short descriptor
    int PerfMode     // value written by 04 0F
)
{
    public string Display => $"{Name}  ({SubName})";
}

public static class PpmModes
{
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

    /// <summary>
    /// The three profiles shown in the UI: 智能 / 高能 / 狂战.
    /// Edit the PerfMode values here to remap the on-device behavior.
    /// </summary>
    public static readonly PerfProfile[] Profiles =
    {
        new PerfProfile("smart", "智能", "Smart / Balanced", 0),
        new PerfProfile("high",  "高能", "High Energy",      1),
        new PerfProfile("beast", "狂战", "Rage / Beast",     3),
    };

    /// <summary>Perf mode that PPM is only accepted under (see Set-SPPM.ps1).</summary>
    public const int BeastPerfMode = 3;

    /// <summary>Minimum labbable PPM level.</summary>
    public const int PpmMin = 0;
    /// <summary>Maximum labbable PPM level.</summary>
    public const int PpmMax = 4;
}
