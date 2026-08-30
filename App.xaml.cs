using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HonorHelper;

public partial class App : Application
{
    private Window? _window;
    private TrayIcon? _tray;
    private IntPtr _hIcon;
    private bool _exitRequested;
    private static Mutex? _instanceMutex;

    public App()
    {
        InitializeComponent();

        // G-Helper 风格浅色主题。README 有记录：<Application RequestedTheme> 属性
        // 会让旧版 XAML 编译器崩溃，所以只能在代码里设置。
        try
        {
            RequestedTheme = ApplicationTheme.Light;
        }
        catch
        {
            // 已有内容初始化时设置会抛异常；此时由 Root 元素的 RequestedTheme 兜底
        }

        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 自动申请管理员权限：正常路径由 manifest (requireAdministrator) 保证；
        // 若以非管理员运行（如开发用 asInvoker 构建），自动弹 UAC 提权重启。
        // 带 --no-elevate 参数或用户取消 UAC 时，降级为非管理员继续运行（界面会警告）。
        if (!IsAdmin() && !Environment.GetCommandLineArgs().Contains("--no-elevate"))
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                    // 新实例已拉起（UAC 通过后为管理员）；当前非管理员实例立即退出。
                    // 注意此时窗口尚未创建，ExitApp 的 Close() 无法结束进程，必须硬退。
                    Environment.Exit(0);
                }
                catch
                {
                    // 用户取消 UAC → 降级运行
                }
            }
        }

        // 单实例：命名 Mutex 由内核保证原子性（进程名枚举有 TOCTOU 竞态，不可靠）。
        // 必须放在提权检查之后：提权路径拉起管理员子进程后自己退出，若原实例
        // 先持有 Mutex，子进程会误判为第二实例而退出，应用永远起不来。
        _instanceMutex = new Mutex(true, @"Local\honor-helper.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // 已有实例在跑：把它从托盘唤出来，然后硬退（只 return 会留下无窗口的僵尸进程）
            TrayIcon.NotifyExistingInstance();
            Environment.Exit(0);
        }

        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();

        CreateTray();
    }

    private static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private void CreateTray()
    {
        _hIcon = LoadIconFromExe();
        _tray = new TrayIcon(_hIcon, "H-Helper — HONOR 控制中心");
        _tray.LeftClick += ToggleWindow;
        _tray.RightClick += ShowTrayMenu;
        _tray.ActivateRequested += ShowMainWindow;
    }

    private static IntPtr LoadIconFromExe()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var h = ExtractIcon(IntPtr.Zero, exe, 0);
            if (h != IntPtr.Zero)
                return h;
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private void ToggleWindow()
    {
        if (_window is null)
            return;

        var appWindow = _window.AppWindow;
        if (appWindow.IsVisible)
        {
            appWindow.Hide();
        }
        else
        {
            appWindow.Show();
            _window.Activate();
        }
    }

    // 第二实例启动时唤出已有实例的窗口：总是显示，不是 ToggleWindow 的显示/隐藏切换
    private void ShowMainWindow()
    {
        if (_window is null)
            return;

        _window.AppWindow.Show();
        _window.Activate();
    }

    private void ShowTrayMenu()
    {
        // 托盘菜单失败绝不能带崩整个进程（WndProc 里的未处理异常会终止应用）
        try
        {
            if (_tray is null)
                return;

            var p = GetCursorPos();
            var chosen = _tray.ShowContextMenu(p.X, p.Y,
                (1, "显示 / 隐藏"),
                (2, "退出"));

            if (chosen == 1) ToggleWindow();
            else if (chosen == 2) ExitApp();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppData.DataDir, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  tray menu: {ex}{Environment.NewLine}");
            }
            catch
            {
                // logger must not throw
            }
        }
    }

    private void ExitApp()
    {
        _exitRequested = true;
        _tray?.Dispose();
        _tray = null;
        _window?.Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // the red X hides to tray instead of exiting
        if (!_exitRequested && _tray is not null && _window is not null)
        {
            args.Handled = true;
            _window.AppWindow.Hide();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
                File.AppendAllText(Path.Combine(AppData.DataDir, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // never throw from the logger
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static (int X, int Y) GetCursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }
}
