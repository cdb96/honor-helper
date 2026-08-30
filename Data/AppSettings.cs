using System;
using System.IO;
using System.Text.Json;

namespace HonorHelper;

/// <summary>全局设置：监控采样率等，存于 %LOCALAPPDATA%\honor-helper\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>温度 / 风扇采样间隔（秒）。</summary>
    public int TempPollSeconds { get; set; } = 5;

    /// <summary>程序联动的进程扫描间隔（秒）。</summary>
    public int ProcScanSeconds { get; set; } = 3;
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppData.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时用默认值，避免带崩界面
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonContext.Default.AppSettings));
        }
        catch
        {
            // 保存失败不影响运行，下次改动再试
        }
    }
}
