using System.Collections.Generic;

namespace HonorHelper.Protocol;

/// <summary>A single temperature reading: channel name -> °C.</summary>
public sealed record TempReading(string Name, int DegreeC);

/// <summary>A single fan reading: id -> rpm (+ optional target/limit rpm).</summary>
public sealed record FanReading(int Id, int Rpm, int? Limit);

/// <summary>GPU clock/utilization snapshot.</summary>
public sealed record GpuStats(int ClockMhz, int UtilPct);

/// <summary>GPU clock-fix result (NvAPI status + whether nvidia-smi -rgc ran).</summary>
public sealed record GpuFixResult(int NvapiStatus, int ClockResetOk, int SmiFound, string Log);

/// <summary>Overclock availability info.</summary>
public sealed record OcInfo(bool Available, string? LastError);

/// <summary>
/// The full observable state snapshot published by the service. The UI polls this
/// at its sampling interval and renders it — no per-call hardware requests needed
/// for reading.
/// </summary>
public sealed record StatusSnapshot(
    int Mode,                                // current perf mode (0..4), -1 unknown
    IReadOnlyList<TempReading> Temps,        // name -> °C
    IReadOnlyList<FanReading> Fans,          // id -> rpm
    int? Touchpad,                           // 1 on / 0 off / null unknown
    int? CpuFreqMhz,                         // current CPU avg frequency (MHz)
    GpuStats? Gpu,                           // GPU clock/util (null if unavailable)
    bool IsAdmin,                           // service running elevated
    int? LastPpm);                          // last PPM written to EC (null unknown; EC has no readback)

/// <summary>Settings editable from the UI.</summary>
public sealed record SettingsDto(int TempPollSeconds);

/// <summary>A program-link rule as seen on the wire.</summary>
public sealed record TriggerDto(string Path, string OpenAction, int OpenPpm,
    string CloseAction, string OpenTouchpad, string CloseTouchpad,
    int GpuCoreMhz, int GpuMemMhz, bool GpuFixOnExit, bool Enabled);

/// <summary>Edit envelope for a single trigger (used by SaveTriggers batch).</summary>
public sealed record TriggerEditDto(IReadOnlyList<TriggerDto> Items);

/// <summary>Apply a performance profile (perf mode + optional PPM).</summary>
public sealed record ApplyProfileDto(string ProfileId, int? Ppm);

/// <summary>Apply a GPU clock offset (domain + MHz).</summary>
public sealed record OcOffsetDto(int ClockDomain, int OffsetMhz);

/// <summary>Generic success/error result for most commands.</summary>
public sealed record SimpleResult(bool Ok, string Message);

/// <summary>Current perf mode value.</summary>
public sealed record ModeResult(int Mode);
