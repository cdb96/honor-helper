using System;
using System.IO;

namespace HonorHelper;

/// <summary>%LOCALAPPDATA%\honor-helper 数据目录；首次访问时把旧目录 HonorPerf 整体搬过来（改名前的配置迁移）。</summary>
internal static class AppData
{
    public static string DataDir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(root, "honor-helper");
            try
            {
                var legacy = Path.Combine(root, "HonorPerf");
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                    Directory.Move(legacy, dir);
            }
            catch
            {
                // 迁移失败不阻塞启动，按新目录继续（大不了重设一次采样率/联动规则）
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
