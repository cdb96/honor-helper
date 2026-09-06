using System;
using System.IO.Pipes;
using System.Threading.Tasks;
using HonorHelper.Protocol;

namespace HonorHelper;

/// <summary>
/// The UI's only bridge to the hardware: a thin named-pipe client to the background
/// <c>honor-helper-service</c>. Every method opens a short-lived pipe connection,
/// sends one request, and reads one reply (matching the service's one-shot model).
/// The UI never touches the native DLL or WMI directly.
/// </summary>
public sealed class ServiceClient
{
    private const int TimeoutMs = 3000;

    public static string PipePath => PipeProtocol.PipeAddress;

    public static bool IsServiceRunning()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName,
                PipeDirection.InOut);
            pipe.Connect(500);
            return pipe.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    // ---- typed RPC helpers ----

    public async Task<bool> PingAsync()
        => await SendAsync(RequestType.Ping).ConfigureAwait(false) is SimpleResult r && r.Ok;

    public async Task<StatusSnapshot?> GetSnapshotAsync()
        => await SendAsync(RequestType.GetSnapshot).ConfigureAwait(false) as StatusSnapshot;

    public async Task<SimpleResult> ApplyProfileAsync(string profileId, int? ppm)
    {
        var dto = new ApplyProfileDto(profileId, ppm);
        return (await SendAsync(RequestType.ApplyProfile, dto).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    public async Task<SimpleResult> SetPpmAsync(int level)
    {
        var payload = BitConverter.GetBytes(level);
        return (await SendAsync(RequestType.SetPpm, payload).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    public async Task<SimpleResult> SetTouchpadAsync(bool on)
    {
        var payload = new[] { (byte)(on ? 1 : 0) };
        return (await SendAsync(RequestType.SetTouchpad, payload).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    public async Task<SimpleResult> SetPerfModeAsync(int mode)
    {
        var payload = BitConverter.GetBytes(mode);
        return (await SendAsync(RequestType.SetPerfMode, payload).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    public async Task<Protocol.GpuFixResult?> RunGpuFixAsync(bool skipClockReset)
    {
        var payload = new[] { (byte)(skipClockReset ? 1 : 0) };
        return await SendAsync(RequestType.RunGpuFix, payload).ConfigureAwait(false) as Protocol.GpuFixResult;
    }

    public async Task<OcInfo?> GetOcInfoAsync()
        => await SendAsync(RequestType.GetOcInfo).ConfigureAwait(false) as OcInfo;

    public async Task<SimpleResult> ApplyOcOffsetAsync(int clockDomain, int offsetMhz)
    {
        var dto = new OcOffsetDto(clockDomain, offsetMhz);
        return (await SendAsync(RequestType.ApplyOcOffset, dto).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    public async Task<SettingsDto?> GetSettingsAsync()
        => await SendAsync(RequestType.GetSettings).ConfigureAwait(false) as SettingsDto;

    public async Task<SimpleResult> SetSettingsAsync(SettingsDto dto)
        => (await SendAsync(RequestType.SetSettings, dto).ConfigureAwait(false) as SimpleResult)
           ?? new SimpleResult(false, "服务无响应");

    public async Task<System.Collections.Generic.List<TriggerDto>?> GetTriggersAsync()
        => await SendAsync(RequestType.GetTriggers).ConfigureAwait(false) as System.Collections.Generic.List<TriggerDto>;

    public async Task<SimpleResult> SaveTriggersAsync(System.Collections.Generic.IReadOnlyList<TriggerDto> items)
    {
        var dto = new TriggerEditDto(items);
        return (await SendAsync(RequestType.SaveTriggers, dto).ConfigureAwait(false) as SimpleResult)
               ?? new SimpleResult(false, "服务无响应");
    }

    // ---- core send/recv ----

    /// <summary>Send one request; returns the deserialized reply payload (or null on error).</summary>
    public async Task<object?> SendAsync(RequestType kind, object? payload = null)
    {
        return await Task.Run(() => SendSync(kind, payload)).ConfigureAwait(false);
    }

    public object? SendSync(RequestType kind, object? payload = null)
    {
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut);
        try
        {
            pipe.Connect(TimeoutMs);
        }
        catch (Exception)
        {
            return null;
        }

        byte[] payloadBytes;
        if (payload is byte[] raw)
            payloadBytes = raw;
        else if (payload is null)
            payloadBytes = Array.Empty<byte>();
        else
            payloadBytes = PipeProtocol.Serialize(payload);

        PipeProtocol.WriteRequestFrame(pipe, kind, payloadBytes);

        try
        {
            var reply = PipeProtocol.ReadReplyFrame(pipe);
            if (!reply.Ok)
                return null;
            // Map reply kind -> concrete deserialized type.
            return DeserializeReply(kind, reply.Payload);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private object? DeserializeReply(RequestType kind, byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
            return null;
        return kind switch
        {
            RequestType.GetSnapshot => PipeProtocol.Deserialize<StatusSnapshot>(payload),
            RequestType.ApplyProfile or RequestType.SetPpm or RequestType.SetTouchpad
                or RequestType.SetPerfMode or RequestType.ApplyOcOffset
                or RequestType.SetSettings or RequestType.SaveTriggers or RequestType.Ping
                => PipeProtocol.Deserialize<SimpleResult>(payload),
            RequestType.RunGpuFix => PipeProtocol.Deserialize<Protocol.GpuFixResult>(payload),
            RequestType.GetOcInfo => PipeProtocol.Deserialize<OcInfo>(payload),
            RequestType.GetSettings => PipeProtocol.Deserialize<SettingsDto>(payload),
            RequestType.GetTriggers => PipeProtocol.Deserialize<System.Collections.Generic.List<TriggerDto>>(payload),
            _ => null,
        };
    }
}
