# 架构：后台服务 + UI 客户端

```
core/                  # C++ 硬件核心（HONOR WMI / NVAPI / nvidia-smi），静态链接进 service
  include/honor_core.h     内部接口声明（HONOR_CORE_EXPORTS 时 dllexport）
  honor_core.cpp           实现（无独立 DLL 产物）

common/                # C# 共享库（仅 UI 使用）：IPC 协议 / DTO / 通道表 / 持久化模型
  Protocol/                命名管道协议 + 请求/应答 DTO
  PpmModes.cs, CoreTables.cs   性能模式与温度/风扇通道表
  AppSettings.cs, ProgramTriggers.cs  配置模型

service/               # 纯 C++ 后台服务（headless，管理员权限，持有托盘图标）
  src/                     json.cpp      无依赖 UTF-8 JSON 解析/序列化
                           protocol.h    管道帧格式 + wire DTO（camelCase JSON）
                           hardware.*    硬件封装（快照/PPM/触控板/GPU OC/锁频修复）
                           stores.*      settings.json / triggers.json 持久化
                           pipe_server.* 中等完整性命名管道服务
                           trigger_engine.* 程序联动后台引擎（3s 轮询进程）
                           tray.*        Win32 托盘图标（显示/隐藏 UI、退出服务）
                           ui_coordinator.* 从托盘拉起/关闭 UI 进程
                           autostart.*   HKCU Run 登录自启
                           main.cpp      入口：服务 + 托盘消息循环 / --install|--uninstall
  tests/pipe_smoke.cpp     管道协议冒烟测试（Ping/设置/联动/快照/未知请求）
  build.ps1                用 MSVC + Windows SDK 构建 honor-helper-service.exe
  app.manifest             requireAdministrator
  Assets/app.ico           托盘/程序图标

ui/                    # WinUI 3 客户端（无需管理员，窗口关闭即进程退出）
  ServiceClient.cs         命名管道客户端，唯一硬件访问通道
  ServiceManager.cs        检测/启动后台服务、登录自启
  MainWindow/SettingsWindow  界面与设置
  MessageWindow.cs(common) 接收服务托盘 显示/隐藏/退出 命令
  honor-helper.csproj
```

构建：`./build.ps1 -c Release`（先编译 C++ 后台服务，再 publish UI 并打包 zip 到 `dist/`）。

## 运行模型

- **托盘图标由服务持有**：服务常驻后台，右上角托盘菜单含「显示 / 隐藏 UI」与「退出服务」。
- **UI 是瞬态客户端**：窗口关闭 = 进程退出（不再隐藏到托盘）；用托盘「显示/隐藏 UI」可重新拉起或关闭它。
- **UI 不需要管理员**：`ui/app.manifest` 为 `asInvoker`，它通过命名管道与后台服务通信。
- **后台服务默认管理员**：`service/app.manifest` 为 `requireAdministrator`，持有所有硬件操作（HONOR WMI / NVAPI / nvidia-smi），并常驻后台。
- **命名管道**：服务用 `CreateNamedPipe` 创建带**中等完整性标签**的管道，使同一用户的非提权 UI 能够连接（普通 .NET 管道在提权进程下默认拒绝非提权访问）。
- **自启**：UI 启动时确保服务在运行；设置/命令行 `--install` 注册登录自启（HKCU Run）。
