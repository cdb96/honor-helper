using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using System.IO;

namespace HonorHelper;

public partial class App : Application
{
    private Window? _window;
    private MessageWindow? _msgWindow;
    private static Mutex? _instanceMutex;

    public App()
    {
        InitializeComponent();

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
        // 单实例：命名 Mutex 由内核保证原子性。若已有 UI 进程（由服务托盘拉起），
        // 通知服务把它唤出，然后退出本实例。
        _instanceMutex = new Mutex(true, @"Local\honor-helper.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // 另一个 UI 已在跑：让它显示出来，然后退出。
            UiControlMessages.Broadcast(UiControlMessages.Show);
            Environment.Exit(0);
        }

        // 创建隐藏消息窗口以接收服务(托盘)的 显示/隐藏/退出 命令。
        _msgWindow = new MessageWindow();
        _msgWindow.ShowRequested += ShowMainWindow;
        _msgWindow.HideRequested += HideMainWindow;
        _msgWindow.QuitRequested += ExitApp;
        _msgWindow.StateChanged += RefreshMainWindow;

        // 确保后台服务在运行（若尚未运行则启动它）。UI 本身不需要管理员权限。
        _ = ServiceManager.EnsureServiceAsync();

        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private void ShowMainWindow()
    {
        if (_window is not null)
        {
            _window.AppWindow.Show();
            _window.Activate();
            // 托盘唤出时数据可能是几分钟前的旧快照：主动拉一次，
            // 免得干等下一轮 3s 轮询（冷启动走构造函数里的 RefreshStateAsync）。
            if (_window is MainWindow mainWindow)
                mainWindow.RefreshNow();
        }
    }

    private void HideMainWindow()
    {
        // 符合「窗口关闭 = 进程退出」：托盘“隐藏 UI”即关闭窗口，从而结束进程。
        ExitApp();
    }

    private void RefreshMainWindow()
    {
        if (_window is MainWindow mainWindow)
            mainWindow.RefreshNow();
    }

    private void ExitApp()
    {
        if (_window is MainWindow mainWindow)
            mainWindow.Shutdown();
        _window?.Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 窗口关闭 = 进程退出：不再隐藏到托盘。托盘由服务持有。
        if (_window is not null)
            _window.Closed -= OnWindowClosed;
        _window = null;

        _msgWindow?.Dispose();
        _msgWindow = null;

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        // 结束进程
        Environment.Exit(0);
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
}
