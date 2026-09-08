using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace HonorHelper.Protocol;

/// <summary>
/// C# AOT-safe JSON context for the wire protocol shared by the UI client and the
/// background service. With NativeAOT the reflection-based serializer is unusable,
/// so every type that crosses the named-pipe boundary must be registered here.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(TriggerDto))]
[JsonSerializable(typeof(TriggerEditDto))]
[JsonSerializable(typeof(ApplyProfileDto))]
[JsonSerializable(typeof(OcOffsetDto))]
[JsonSerializable(typeof(ChargeThresholdDto))]
[JsonSerializable(typeof(SimpleResult))]
[JsonSerializable(typeof(ModeResult))]
[JsonSerializable(typeof(GpuStats))]
[JsonSerializable(typeof(GpuFixResult))]
[JsonSerializable(typeof(OcInfo))]
[JsonSerializable(typeof(List<TriggerDto>))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
