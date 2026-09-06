namespace HonorHelper.Protocol;

/// <summary>RPC command the UI sends to the service.</summary>
public enum RequestType
{
    GetSnapshot = 0,
    ApplyProfile = 1,
    SetPpm = 2,
    SetTouchpad = 3,
    SetPerfMode = 4,
    RunGpuFix = 5,
    GetGpuStats = 6,
    GetOcInfo = 7,
    ApplyOcOffset = 8,
    GetSettings = 9,
    SetSettings = 10,
    GetTriggers = 11,
    SaveTriggers = 12,
    RunTriggerAction = 13,
    Ping = 14,
}
