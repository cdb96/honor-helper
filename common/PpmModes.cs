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

/// <summary>
/// Thick convenience accessor over the shared <see cref="CoreTables"/> profile data,
/// used by both the UI and the service. Kept for backward-compatible call sites.
/// </summary>
public static class PpmModes
{
    public static System.Collections.Generic.IReadOnlyDictionary<int, string> PpmNames
        => CoreTables.PpmNames;

    public static System.Collections.Generic.IReadOnlyDictionary<int, string> PerfModeNames
        => CoreTables.PerfModeNames;

    public static PerfProfile[] Profiles => CoreTables.Profiles;

    public const int BeastPerfMode = CoreTables.BeastPerfMode;

    public const int PpmMin = 0;
    public const int PpmMax = 4;
}
