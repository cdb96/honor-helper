using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HonorHelper.Protocol;

/// <summary>
/// Shared named-pipe transport between the UI client and the background service.
/// A single message is a self-describing, length-prefixed frame:
///   [int32 kind][int32 payloadLen][payload = UTF-8 JSON]
/// Because the payload type is implied by <see cref="RequestType"/>, no polymorphic
/// <c>object</c> serialization is needed — this is fully NativeAOT-safe.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = @"honor-helper.svc";
    public const string PipeAddress = @"\\.\pipe\" + PipeName;

    /// <summary>One framed request+payload. Payload is the JSON bytes of the concrete request type.</summary>
    public readonly record struct RequestFrame(RequestType Kind, byte[] Payload);

    /// <summary>One framed reply+payload. Kind echoes the request, Payload is the JSON reply.</summary>
    public readonly record struct ReplyFrame(RequestType Kind, bool Ok, string? Error, byte[]? Payload);

    public const int MaxMessage = 64 * 1024 * 1024; // 64 MB safety cap

    // ---- framing ----

    public static void WriteRequestFrame(Stream s, RequestType kind, byte[] payload)
    {
        WriteInt(s, (int)kind);
        WriteInt(s, payload.Length);
        s.Write(payload, 0, payload.Length);
        s.Flush();
    }

    public static RequestFrame ReadRequestFrame(Stream s, CancellationToken ct = default)
    {
        int kind = ReadInt(s, ct);
        int len = ReadInt(s, ct);
        ValidateLen(len);
        var payload = ReadBytes(s, len, ct);
        return new RequestFrame((RequestType)kind, payload);
    }

    public static void WriteReplyFrame(Stream s, RequestType kind, bool ok, string? error, byte[]? payload)
    {
        WriteInt(s, (int)kind);
        WriteInt(s, ok ? 1 : 0);
        var errBytes = error is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(error);
        WriteInt(s, errBytes.Length);
        if (errBytes.Length > 0)
            s.Write(errBytes, 0, errBytes.Length);
        var plen = payload?.Length ?? 0;
        WriteInt(s, plen);
        if (plen > 0)
            s.Write(payload!, 0, plen);
        s.Flush();
    }

    public static ReplyFrame ReadReplyFrame(Stream s, CancellationToken ct = default)
    {
        int kind = ReadInt(s, ct);
        int ok = ReadInt(s, ct);
        int errLen = ReadInt(s, ct);
        ValidateLen(errLen, 1 << 20);
        var err = errLen > 0 ? Encoding.UTF8.GetString(ReadBytes(s, errLen, ct)) : null;
        int plen = ReadInt(s, ct);
        ValidateLen(plen);
        var payload = plen > 0 ? ReadBytes(s, plen, ct) : null;
        return new ReplyFrame((RequestType)kind, ok != 0, err, payload);
    }

    // ---- JSON (source-gen) helpers ----

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), WireJsonContext.Default);

    public static T? Deserialize<T>(byte[] payload)
        => (T?)JsonSerializer.Deserialize(payload, typeof(T), WireJsonContext.Default);

    // ---- byte helpers (little-endian) ----

    private static void WriteInt(Stream s, int value)
    {
        var b = BitConverter.GetBytes(value);
        s.Write(b, 0, 4);
    }

    private static int ReadInt(Stream s, CancellationToken ct)
    {
        var b = new byte[4];
        ReadExactly(s, b, 0, 4, ct);
        return BitConverter.ToInt32(b, 0);
    }

    private static byte[] ReadBytes(Stream s, int len, CancellationToken ct)
    {
        var buf = new byte[len];
        if (len > 0)
            ReadExactly(s, buf, 0, len, ct);
        return buf;
    }

    private static void ReadExactly(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, offset + read, count - read);
            if (n == 0)
                throw new EndOfStreamException("Pipe closed before a full message was read.");
            read += n;
        }
    }

    private static void ValidateLen(int len, int cap = MaxMessage)
    {
        if (len < 0 || len > cap)
            throw new InvalidDataException($"Invalid length: {len}");
    }
}
