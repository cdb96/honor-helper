using System.Text.Json.Serialization;

namespace HonorHelper;

/// <summary>
/// NativeAOT（PublishAot）下反射式 JsonSerializer 不可用（IL3050/IL2026），
/// 所有 JSON 持久化类型必须在此登记，用 source-gen 替代运行时反射。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ProgramTrigger>))]
internal sealed partial class JsonContext : JsonSerializerContext;
