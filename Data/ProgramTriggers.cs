using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HonorHelper;

/// <summary>一条程序联动规则：监控指定 exe，在其启动/退出时执行对应动作。</summary>
public sealed class ProgramTrigger
{
    public string Path { get; set; } = "";

    /// <summary>打开时切换的性能模式（none/smart/high/beast）。</summary>
    public string OpenAction { get; set; } = "none";

    /// <summary>打开动作选「狂战」时同时设置的 PPM 级别（0–4，默认 2）。</summary>
    public int OpenPpm { get; set; } = 2;

    /// <summary>退出时切换的性能模式。</summary>
    public string CloseAction { get; set; } = "none";

    /// <summary>打开时对触控板的动作（none/tp_on/tp_off）。</summary>
    public string OpenTouchpad { get; set; } = "none";

    /// <summary>退出时对触控板的动作。</summary>
    public string CloseTouchpad { get; set; } = "none";

    /// <summary>程序运行期间的 GPU 核心频率偏移（MHz，可负=降频）。打开时应用、退出自动归零。</summary>
    public int GpuCoreMhz { get; set; } = 0;

    /// <summary>程序运行期间的 GPU 显存频率偏移（MHz）。打开时应用、退出自动归零。</summary>
    public int GpuMemMhz { get; set; } = 0;

    /// <summary>程序退出并完成动作后，自动执行一次 GPU 锁频修复（恢复被游戏场景钉住的频率）。</summary>
    public bool GpuFixOnExit { get; set; } = false;

    /// <summary>停用的规则保留但不触发（旧配置文件无此字段时反序列化为 true）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>程序联动配置的持久化（%LOCALAPPDATA%\honor-helper\triggers.json）与动作定义。</summary>
public static class ProgramTriggers
{
    /// <summary>性能模式动作（性能模式下拉框选项）。</summary>
    public static readonly (string Id, string Label)[] ModeActions =
    {
        ("none",  "无操作"),
        ("smart", "切换智能模式"),
        ("high",  "切换高能模式"),
        ("beast", "切换狂战模式"),
    };

    /// <summary>触控板动作（触控板下拉框选项）。</summary>
    public static readonly (string Id, string Label)[] TouchpadActions =
    {
        ("none",   "无操作"),
        ("tp_on",  "开启触控板"),
        ("tp_off", "关闭触控板"),
    };

    /// <summary>动作 id -> 显示名（在模式与触控板两张表里查找）。</summary>
    public static string ActionLabel(string id)
    {
        foreach (var (aid, label) in ModeActions)
        {
            if (aid == id)
                return label;
        }
        foreach (var (aid, label) in TouchpadActions)
        {
            if (aid == id)
                return label;
        }
        return "无操作";
    }

    private static string FilePath => Path.Combine(AppData.DataDir, "triggers.json");

    public static List<ProgramTrigger> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<ProgramTrigger>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, JsonContext.Default.ListProgramTrigger) ?? new List<ProgramTrigger>();
        }
        catch
        {
            // 配置损坏时从空列表开始，避免带崩界面
            return new List<ProgramTrigger>();
        }
    }

    public static void Save(IReadOnlyList<ProgramTrigger> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonContext.Default.ListProgramTrigger));
        }
        catch
        {
            // 配置写失败不影响运行，下次改动再试
        }
    }
}
