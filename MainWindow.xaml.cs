using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HonorHelper;

public sealed partial class MainWindow : Window
{
    // G-Helper 风格浅色配色：选中描边（性能区绿色），其余白底细灰边
    private const string FgHex = "#1A1A1A";
    private const string TileBorderHex = "#D8D8DC";
    private const string SelectGreenHex = "#0FA05A";
    private const string AccentBlueHex = "#0F6CBD";

    /// <summary>窗口默认高度（物理像素）。内容超出时由 ScrollViewer 滚动；
    /// 不随 PPM 面板开合改变窗口大小，避免重置用户手动调整的尺寸。</summary>
    private const int CollapsedHeight = 820;
    private const int ExpandedHeight = 1020;

    private readonly PpmController _controller = new();
    private readonly DispatcherTimer _ppmDebounce = new();
    private readonly DispatcherTimer _tempTimer = new();
    private readonly DispatcherTimer _procTimer = new();
    private readonly List<ProgramTrigger> _triggers = new();
    private readonly AppSettings _settings = new();
    private SettingsWindow? _settingsWindow;
    private readonly HashSet<string> _runningProcs = new(StringComparer.OrdinalIgnoreCase);
    private bool _initializing = true;
    private string? _activeModeId;
    private bool _busy;
    private bool _ppmOpen;
    private bool _tempRefreshPending;
    private bool _procScanPending;
    private bool _touchpadSync;
    private bool _touchpadBusy;
    private string? _modeStatusText;
    private readonly DispatcherTimer _statusRevertTimer = new();

    // 轮询省电：值没变就不碰视觉树
    private readonly List<int> _lastTemps = new();
    private int? _lastTouchpadState;
    private int _lastAppliedMode = int.MinValue;
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> BrushCache = new();
    public MainWindow()
    {
        InitializeComponent();
        Title = "H-Helper — HONOR WIN H7";

        AppWindow.Resize(new Windows.Graphics.SizeInt32(600, CollapsedHeight));

        // 无边框观感：隐藏系统标题栏，HeaderDrag 作为拖动区
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyCaptionButtonColors();
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = this.AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            // 设置系统标题栏高尺寸模式（高尺寸高度正好满足大图标和居中对齐）
            titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        }

        _ppmDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _ppmDebounce.Tick += (_, _) =>
        {
            _ppmDebounce.Stop();
            _ = ApplyPpmFromSliderAsync();
        };

        // 瞬时状态提示 4 秒后回落到最近一条持久状态行（当前模式）
        _statusRevertTimer.Interval = TimeSpan.FromSeconds(4);
        _statusRevertTimer.Tick += (_, _) =>
        {
            _statusRevertTimer.Stop();
            if (_modeStatusText is not null)
                StatusText.Text = _modeStatusText;
        };

        // 设置：采样率等（%LOCALAPPDATA%\honor-helper\settings.json，在设置窗口调整）
        _settings = SettingsStore.Load();

        // 温度轮询：G-Helper 风格，标题行右侧常驻 CPU/GPU 温度；间隔可在设置窗口调整
        _tempTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.TempPollSeconds));
        _tempTimer.Tick += async (_, _) => await RefreshTempsAsync();
        _tempTimer.Start();

        // 程序联动：加载配置 + 按设置间隔轮询进程，检测联动程序的启动/退出沿
        // （联动规则的增删改在「⚙️ 设置」窗口里进行）
        _triggers.AddRange(ProgramTriggers.Load());
        _procTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ProcScanSeconds));
        _procTimer.Tick += async (_, _) => await ScanProcessesAsync();
        _procTimer.Start();

        // 瓷砖图标/文字随窗口宽度自适应缩放
        Root.SizeChanged += OnRootSizeChanged;
        UpdateTileScale(Root.ActualWidth);

        HighlightModes(null);
        HighlightPpm(-1);
        _ = RefreshStateAsync();
        _initializing = false;
    }

    // ---------- helpers ----------

    private PerfProfile? ProfileForId(string id)
        => PpmModes.Profiles.FirstOrDefault(pr => pr.Id == id);

    /// <summary>性能模式瓷砖（不含 PPM 开关，它是独立的面板开关）。</summary>
    private Button[] ModeButtons => new[] { BtnModeSmart, BtnModeHigh, BtnModeBeast };

    /// <summary>PPM 面板里的刻度按钮 0..4。</summary>
    private Button[] PpmButtons => new[] { BtnPpm0, BtnPpm1, BtnPpm2, BtnPpm3, BtnPpm4 };

    private static string ReadTag(Button b) => b.Tag as string ?? string.Empty;

    /// <summary>Serializes WMI work and blocks re-entrant clicks while one is running.</summary>
    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy)
            return;
        _busy = true;
        SetBusy(true);
        try
        {
            await work();
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var b in ModeButtons.Concat(PpmButtons).Concat(new[] { BtnRefresh }))
            b.IsEnabled = !busy;
        PpmSlider.IsEnabled = !busy;
        if (busy)
            SetStatus("正在切换…");
    }

    // ---------- 状态栏 ----------

    /// <summary>持久状态行：当前模式读数、权限/WMI 错误等，不会自动消失。</summary>
    private void SetModeStatus(string text)
    {
        _modeStatusText = text;
        _statusRevertTimer.Stop();
        StatusText.Text = text;
    }

    /// <summary>瞬时提示：4 秒后自动回落到最近一条持久状态行。</summary>
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _statusRevertTimer.Stop();
        _statusRevertTimer.Start();
    }

    private string ModeStatusText(int mode)
        => $"当前：perf {mode} · {(PpmModes.PerfModeNames.TryGetValue(mode, out var n) ? n : "未知")}";

    /// <summary>
    /// 刷新模式状态行。force=true 直接写入（启动/手动刷新）；
    /// 轮询路径 force=false：状态栏正显示模式行时就地更新，正在显示瞬时提示时只更新回落目标。
    /// </summary>
    private void UpdateModeStatus(int mode, bool force)
    {
        _modeStatusText = ModeStatusText(mode);
        if (force)
        {
            _statusRevertTimer.Stop();
            StatusText.Text = _modeStatusText;
        }
        else if (StatusText.Text?.StartsWith("当前：", StringComparison.Ordinal) == true)
        {
            StatusText.Text = _modeStatusText;
        }
    }

    /// <summary>选中 = 彩色描边（G-Helper 不用底色填充），未选中 = 细灰边。</summary>
    private void HighlightModes(string? activeId)
    {
        foreach (var b in ModeButtons)
        {
            bool on = ReadTag(b) == activeId;
            b.BorderBrush = SolidColorBrush(on ? SelectGreenHex : TileBorderHex);
            b.BorderThickness = new Thickness(on ? 2 : 1);
            b.Foreground = SolidColorBrush(FgHex);
        }
    }

    private void HighlightPpm(int level)
    {
        for (int i = 0; i < PpmButtons.Length; i++)
        {
            bool on = i == level;
            PpmButtons[i].Foreground = SolidColorBrush(on ? AccentBlueHex : "#70707A");
            PpmButtons[i].FontWeight = on
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private static Windows.UI.Color WinColor(string hex)
    {
        // accept "#RRGGBB" and "RRGGBB"
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6)
            throw new FormatException($"Bad color: {hex}");
        return Microsoft.UI.ColorHelper.FromArgb(
            255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush SolidColorBrush(string hex)
    {
        // 画刷缓存：轮询每轮刷新十几个颜色，避免反复分配新画刷导致视觉树失效
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(WinColor(hex));
            BrushCache[hex] = brush;
        }
        return brush;
    }

    private void ApplyCaptionButtonColors()
    {
        // 浅色画布上的标题栏按钮：深色字形 + 悬停浅灰
        try
        {
            var tb = AppWindow.TitleBar;
            tb.ButtonForegroundColor = WinColor(FgHex);
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonHoverBackgroundColor = WinColor("#E8E8EC");
            tb.ButtonHoverForegroundColor = WinColor(FgHex);
            tb.ButtonPressedBackgroundColor = WinColor("#DCDCE1");
            tb.ButtonPressedForegroundColor = WinColor(FgHex);
            tb.ButtonInactiveForegroundColor = WinColor("#9A9AA2");
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch
        {
            // 个别系统版本上标题栏颜色属性可能不可用，忽略即可
        }
    }

    private async Task RefreshStateAsync()
    {
        bool admin = _controller.IsAdmin();
        AdminText.Text = admin ? "" : "非管理员：无法读写 HONOR WMI，请以管理员身份运行。";
        AdminText.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        if (!admin)
        {
            PerfModeRun.Text = "—";
            TempRun.Text = "";
            TempGpuRun.Text = "";
            SetModeStatus("无法读取当前模式（需管理员权限）。");
            return;
        }

        int mode = await _controller.GetCurrentPerfModeAsync();

        if (mode < 0)
        {
            PerfModeRun.Text = "—";
            SetModeStatus("无法读取当前模式（WMI 无响应或设备不支持）。");
            return;
        }

        ApplyPerfModeToUi(mode, force: true);
        SetModeStatus(ModeStatusText(mode));

        await RefreshTempsAsync();
    }

    /// <summary>
    /// 把 perf 模式套到界面（标题、瓷砖高亮、PPM 面板开合）。
    /// 启动/刷新/模式切换与 5 秒轮询共用；轮询路径不写 StatusText，避免覆盖瞬时提示。
    /// </summary>
    private void ApplyPerfModeToUi(int mode, bool force = false)
    {
        if (mode < 0)
        {
            PerfModeRun.Text = "—";
            _lastAppliedMode = mode;
            return;
        }

        // 模式没变就不碰视觉树；启动/手动刷新用 force=true 强制套用（撤销失败的乐观高亮）
        if (!force && mode == _lastAppliedMode)
            return;
        _lastAppliedMode = mode;

        var profile = PpmModes.Profiles.FirstOrDefault(pr => pr.PerfMode == mode);

        PerfModeRun.Text = profile?.Name ?? $"未知(perf {mode})";
        if (profile is not null)
        {
            _activeModeId = profile.Id;
            HighlightModes(profile.Id);
        }

        // PPM 面板只在状态变化时开合，避免轮询反复触发窗口 resize
        bool isBeast = mode == PpmModes.BeastPerfMode;
        if (isBeast && !_ppmOpen)
        {
            ShowPpmPanel();
            HighlightPpm(CurrentPpm);
        }
        else if (!isBeast && _ppmOpen)
        {
            HidePpmPanel();
        }
    }

    private void ShowPpmPanel()
    {
        _ppmOpen = true;
        PpmPanel.Visibility = Visibility.Visible;
        HighlightModes(_activeModeId);
    }

    private void HidePpmPanel()
    {
        _ppmOpen = false;
        PpmPanel.Visibility = Visibility.Collapsed;
        HighlightModes(_activeModeId);
    }

    private int CurrentPpm => (int)Math.Round(PpmSlider.Value);

    private async Task ApplyPpmFromSliderAsync()
    {
        int lvl = CurrentPpm;
        HighlightPpm(lvl);
        bool ok = await _controller.SetPpmAsync(lvl);
        SetStatus(ok
            ? $"PPM 已设为 {lvl} · {LabelForPpm(lvl)}"
            : $"PPM {lvl} 设置失败");
    }

    private static string LabelForPpm(int lvl)
        => PpmModes.PpmNames.TryGetValue(lvl, out var n) ? n : "未知";

    // ---------- 温度 ----------

    /// <summary>温度 ≥ 此值显示红色。</summary>
    private const int TempHotC = 80;

    private string TempColorHex(int t)
        => t >= TempHotC ? "#D13438" : t >= 65 ? "#B25E09" : "#3A3A40";

    private void SetTempRun(Microsoft.UI.Xaml.Documents.Run run, string name, int? t, int? freqMhz = null)
    {
        string text = t is null
            ? ""
            : $"{name}: {t}°C{(freqMhz is > 0 ? $" {freqMhz / 1000.0:0.#}GHz" : "")}  ";
        if (run.Text == text)
            return;   // 无变化不碰视觉树
        run.Text = text;
        if (t is not null)
            run.Foreground = SolidColorBrush(TempColorHex(t.Value));
    }

    /// <summary>温度面板单元格：构造时按通道表生成，刷新时只改文字和颜色。</summary>
    private readonly List<Microsoft.UI.Xaml.Documents.Run> _tempCellRuns = new();
    private DateTime _lastTempRefreshUtc;

    private void BuildTempsGrid()
    {
        if (_tempCellRuns.Count > 0 || TempsGrid is null)
            return;

        for (int i = 0; i < PpmController.TempChannels.Count; i++)
        {
            var ch = PpmController.TempChannels[i];

            var sp = new StackPanel { Spacing = 2 };
            Grid.SetColumn(sp, i % 4);
            Grid.SetRow(sp, i / 4);

            var name = new TextBlock
            {
                Text = ch.Name,
                FontSize = 11,
                Foreground = SolidColorBrush("#8A8A92"),
            };
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = "—" };
            var val = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            val.Inlines.Add(run);

            sp.Children.Add(name);
            sp.Children.Add(val);
            TempsGrid.Children.Add(sp);
            _tempCellRuns.Add(run);
        }

        // 12 个通道 / 4 列 = 3 行
        while (TempsGrid.RowDefinitions.Count < (PpmController.TempChannels.Count + 3) / 4)
            TempsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    }

    /// <summary>拉取全部通道温度并更新标题行与温度面板。带重入保护。</summary>
    private async Task RefreshTempsAsync()
    {
        if (_tempRefreshPending)
            return;
        _tempRefreshPending = true;
        try
        {
            var tempsTask = _controller.GetTempsAsync();
            var cpuFreqTask = _controller.GetCpuFreqMhzAsync();
            var fanTask = _controller.GetFanSpeedsAsync();
            var touchpadTask = _controller.GetTouchpadStateAsync();
            var modeTask = _controller.GetCurrentPerfModeAsync();

            var gpuTask = GpuClockFix.GetGpuStatsAsync();   // NvAPI 进程内查询，开销可忽略
            await Task.WhenAll(tempsTask, cpuFreqTask, fanTask, touchpadTask, modeTask, gpuTask);

            var temps = tempsTask.Result;
            temps.TryGetValue("CPU", out var cpu);
            temps.TryGetValue("GPU", out var gpu);
            SetTempRun(TempRun, "CPU", cpu == 0 ? null : cpu, cpuFreqTask.Result);
            SetTempRun(TempGpuRun, "GPU", gpu == 0 ? null : gpu, gpuTask.Result?.ClockMhz);

            // 温度面板：值没变的格子不碰视觉树，滚动更顺滑
            BuildTempsGrid();
            while (_lastTemps.Count < _tempCellRuns.Count)
                _lastTemps.Add(int.MinValue);
            for (int i = 0; i < PpmController.TempChannels.Count && i < _tempCellRuns.Count; i++)
            {
                var ch = PpmController.TempChannels[i];
                bool ok = temps.TryGetValue(ch.Name, out var t) && t > 0;
                int val = ok ? t : -1;
                if (_lastTemps[i] == val)
                    continue;
                _lastTemps[i] = val;
                _tempCellRuns[i].Text = ok ? $"{t}°C" : "—";
                _tempCellRuns[i].Foreground = SolidColorBrush(ok ? TempColorHex(t) : "#B9B9C0");
            }

            var fans = fanTask.Result;
            SetFanRuns(fans);

            UpdateTouchpadUI(touchpadTask.Result);

            // 自动查询当前性能模式：Fn 键 / 荣耀管家等外部改动也能实时反映；
            // 模式切换进行中（_busy）先不抢占，等切换完成后的刷新兜底
            int mode = modeTask.Result;
            if (!_busy && mode >= 0)
            {
                ApplyPerfModeToUi(mode);
                UpdateModeStatus(mode, force: false);
            }

            _lastTempRefreshUtc = DateTime.UtcNow;
            TempsUpdatedText.Text = _lastTempRefreshUtc.ToLocalTime().ToString("HH:mm:ss");
        }
        catch
        {
            // 温度是辅助信息，读失败保持原样即可
        }
        finally
        {
            _tempRefreshPending = false;
        }
    }

    /// <summary>标题行的风扇转速 Runs，顺序与 PpmController.Fans 一致。</summary>
    private Microsoft.UI.Xaml.Documents.Run[] FanRuns => new[] { FanCpuRun, FanGpuRun, Fan3Run };

    /// <summary>
    /// 「温度」标题行右侧的风扇转速（与 CPU/GPU 温度同款排版）。
    /// 第二个 u16（疑为目标/上限转速）与停转提示放 tooltip，不占版面。
    /// </summary>
    private void SetFanRuns(Dictionary<int, PpmController.FanSpeed> fans)
    {
        var runs = FanRuns;
        for (int i = 0; i < PpmController.Fans.Length && i < runs.Length; i++)
        {
            var (id, name) = PpmController.Fans[i];
            var run = runs[i];
            string text = fans.TryGetValue(id, out var f)
                ? $"{name} {f.Rpm} rpm   "
                : "";
            if (run.Text == text)
                continue;   // 无变化不碰视觉树
            run.Text = text;
        }

        var limits = new List<string>();
        var stopped = new List<string>();
        foreach (var (id, name) in PpmController.Fans)
        {
            if (!fans.TryGetValue(id, out var f))
                continue;
            if (f.Limit is > 0)
                limits.Add($"{name} {f.Limit}");
            if (f.Rpm == 0)
                stopped.Add(name);
        }

        string? tip = null;
        if (limits.Count > 0)
            tip = "第二返回字段（疑为目标/上限转速）：" + string.Join(" · ", limits);
        if (stopped.Count > 0)
            tip = (tip is null ? "" : tip + "  |  ") + string.Join("、", stopped) + " 读数 0（可能为停转）";
        ToolTipService.SetToolTip(FanRunsText, tip);
    }

    // ---------- 程序联动 ----------

    /// <summary>
    /// 轮询进程（3s）：对联动程序检测启动/退出沿并执行对应动作。
    /// 启动时已在运行的程序视为「刚启动」（补发打开动作）。
    /// </summary>
    private async Task ScanProcessesAsync()
    {
        if (_procScanPending || _triggers.Count == 0)
            return;
        _procScanPending = true;
        try
        {
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    now.Add(p.ProcessName);
                    p.Dispose();
                }
            }
            catch
            {
                return; // 枚举失败保持现状，下轮再试
            }

            foreach (var t in _triggers)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(t.Path);
                if (string.IsNullOrEmpty(name))
                    continue;
                bool was = _runningProcs.Contains(name);
                bool isRunning = now.Contains(name);

                if (t.Enabled)
                {
                    if (isRunning && !was && t.OpenAction != "none")
                        await RunTriggerAsync(t, opening: true);
                    else if (!isRunning && was && t.CloseAction != "none")
                        await RunTriggerAsync(t, opening: false);
                }
            }

            _runningProcs.Clear();
            foreach (var n in now)
                _runningProcs.Add(n);
        }
        finally
        {
            _procScanPending = false;
        }
    }

    // ---------- 联动 GPU 超频补偿：打开 +45s 后补写一次（覆盖系统晚到的重置），仅此一次 ----------

    private readonly Dictionary<string, CancellationTokenSource> _offsetRewrite = new(StringComparer.OrdinalIgnoreCase);

    private void ScheduleOffsetRewrite(ProgramTrigger t)
    {
        CancelOffsetRewrite(t.Path);
        if (t.GpuCoreMhz == 0 && t.GpuMemMhz == 0)
            return;

        var cts = new CancellationTokenSource();
        _offsetRewrite[t.Path] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(45_000, cts.Token);
                NvPstatesOc.ApplyOffset(NvPstatesOc.ClockGraphics, t.GpuCoreMhz);
                if (t.GpuMemMhz != 0)
                    NvPstatesOc.ApplyOffset(NvPstatesOc.ClockMemory, t.GpuMemMhz);
            }
            catch (OperationCanceledException) { }
            finally
            {
                CancelOffsetRewrite(t.Path);
            }
        });
    }

    private void CancelOffsetRewrite(string path)
    {
        if (_offsetRewrite.TryGetValue(path, out var cts))
        {
            _offsetRewrite.Remove(path);
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>执行联动动作：切换到对应性能模式并同步界面高亮。</summary>
    private async Task RunTriggerAsync(ProgramTrigger t, bool opening)
    {
        var exeName = System.IO.Path.GetFileName(t.Path);
        string verb = opening ? "检测到" : "已退出";
        var messages = new List<string>();

        if (opening)
            ScheduleOffsetRewrite(t);   // 打开时安排 +30s 补写一次 GPU 超频（覆盖系统晚到的重置）
        else
            CancelOffsetRewrite(t.Path);

        // 1. 性能模式动作
        var modeId = opening ? t.OpenAction : t.CloseAction;
        var profile = PpmModes.Profiles.FirstOrDefault(p => p.Id == modeId);
        if (profile is not null)
        {
            bool ok = await _controller.SetPerfModeAsync(profile.PerfMode);
            messages.Add(ok ? $"已切换{profile.Name}" : $"切换{profile.Name}失败");
            if (ok)
            {
                _activeModeId = profile.Id;
                HighlightModes(profile.Id);
                if (profile.PerfMode == PpmModes.BeastPerfMode)
                    ShowPpmPanel();
                else
                    HidePpmPanel();
            }

            // 打开动作选狂战时，按规则设置 PPM 级别（仅狂战模式可写）。
            // 模式切换在 EC 侧是异步落地的：立即写 PPM 会被模式默认值覆盖，先等一拍；
            // 失败再补一次二次确认。
            if (ok && opening && profile.PerfMode == PpmModes.BeastPerfMode && t.OpenPpm is >= 0 and <= 4)
            {
                await Task.Delay(600);
                bool ppmOk = await _controller.SetPpmAsync(t.OpenPpm);
                if (!ppmOk)
                {
                    await Task.Delay(400);
                    ppmOk = await _controller.SetPpmAsync(t.OpenPpm);
                }
                messages.Add(ppmOk ? $"PPM {t.OpenPpm}" : $"PPM {t.OpenPpm} 设置失败");
                if (ppmOk)
                {
                    PpmSlider.Value = t.OpenPpm;   // 主窗口滑条同步，直观可见
                    HighlightPpm(t.OpenPpm);
                }
            }
        }

        // 2. 触控板动作
        var tpId = opening ? t.OpenTouchpad : t.CloseTouchpad;
        if (tpId is "tp_on" or "tp_off")
        {
            bool tpOn = tpId == "tp_on";
            bool tpOk = await _controller.SetTouchpadStateAsync(tpOn);
            messages.Add(tpOk ? $"触控板已{(tpOn ? "开启" : "关闭")}" : "触控板设置失败");
        }

        if (messages.Count > 0)
            SetStatus($"程序联动：{verb} {exeName} → " + string.Join("，", messages));

        // 3. GPU 超频（NVAPI SetPstates20）：打开时应用，退出时归零
        if (t.GpuCoreMhz != 0 || t.GpuMemMhz != 0)
        {
            int core = opening ? t.GpuCoreMhz : 0;
            int mem = opening ? t.GpuMemMhz : 0;
            var (ocOk, detail) = await Task.Run(() =>
            {
                int rcCore = NvPstatesOc.ApplyOffset(NvPstatesOc.ClockGraphics, core);
                if (rcCore == -1 || rcCore == -2)
                    return (false, $"NVAPI 不可用（{NvPstatesOc.LastError ?? $"rc={rcCore}"}）");
                int rcMem = mem != 0 ? NvPstatesOc.ApplyOffset(NvPstatesOc.ClockMemory, mem) : 0;
                bool ok = rcCore == 0 && rcMem == 0;
                var sb = new System.Text.StringBuilder();
                if (core != 0)
                    sb.Append(rcCore == 0 ? $"核心 {core:+0;-0;0}" : $"核心失败(rc={rcCore})");
                if (mem != 0)
                {
                    // 本机型驱动拒绝显存域偏移（rc=-104），单独说明
                    string memMsg = rcMem == 0
                        ? $"显存 {mem:+0;-0;0}"
                        : rcMem == -104
                            ? "显存不被驱动支持"
                            : $"显存失败(rc={rcMem})";
                    sb.Append(sb.Length > 0 ? "，" : "").Append(memMsg);
                }
                return (ok, sb.ToString());
            });
            messages.Add(ocOk ? $"GPU {detail}" : $"GPU 超频失败：{detail}");
        }

        // 4. 该规则单独开启的「退出时自动 GPU 锁频修复」
        if (!opening && t.GpuFixOnExit)
        {
            var fix = await Task.Run(async () => await GpuClockFix.RunAsync());
            string baseText = messages.Count > 0 ? StatusText.Text : $"程序联动：{verb} {exeName}";
            SetStatus(baseText + (fix.Ok ? "（已自动执行 GPU 锁频修复）" : "（GPU 自动修复失败）"));
        }
    }

    // ---------- 触控板 ----------

    /// <summary>同步触控板开关 UI（随温度轮询回读，Fn 键等外部改动也会反映）。null = 读取失败。</summary>
    private void UpdateTouchpadUI(int? state)
    {
        if (state is not null && state == _lastTouchpadState)
            return;   // 状态没变不碰开关（也避免触发 Toggled）
        _lastTouchpadState = state;

        if (state is null)
        {
            _touchpadSync = true;
            TouchpadToggle.IsOn = false;
            _touchpadSync = false;
            TouchpadToggle.IsEnabled = false;
            TouchpadNoteText.Text = "读取失败（需管理员）";
            return;
        }

        TouchpadToggle.IsEnabled = true;
        _touchpadSync = true;
        TouchpadToggle.IsOn = state == 1;
        _touchpadSync = false;
        TouchpadNoteText.Text = state == 1 ? "已开启" : "已关闭";
    }

    private async void OnTouchpadToggled(object sender, RoutedEventArgs e)
    {
        // 轮询回读引发的 IsOn 变化不回写
        if (_touchpadSync || _touchpadBusy)
            return;
        bool on = TouchpadToggle.IsOn;

        _touchpadBusy = true;
        TouchpadToggle.IsEnabled = false;
        SetStatus(on ? "正在开启触控板…" : "正在关闭触控板…");
        try
        {
            bool ok = await _controller.SetTouchpadStateAsync(on);
            SetStatus(ok
                ? (on ? "触控板已开启" : "触控板已关闭")
                : "触控板设置失败");
        }
        catch (Exception ex)
        {
            SetStatus($"触控板设置失败：{ex.Message}");
        }
        finally
        {
            _touchpadBusy = false;
            // 写完回读真实状态：失败时开关自动弹回
            UpdateTouchpadUI(await _controller.GetTouchpadStateAsync());
        }
    }

    // ---------- event handlers ----------

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not Button b || b.Tag is not string id)
            return;

        var profile = ProfileForId(id);
        if (profile is null)
            return;

        // optimistic UI: highlight + toggle the PPM panel at once
        _activeModeId = id;
        HighlightModes(id);
        if (profile.PerfMode == PpmModes.BeastPerfMode)
            ShowPpmPanel();
        else
            HidePpmPanel();

        int? ppm = profile.PerfMode == PpmModes.BeastPerfMode ? CurrentPpm : null;

        _ = RunBusyAsync(async () =>
        {
            var result = await _controller.ApplyProfileAsync(profile, ppm);
            SetStatus(result.Message);
            if (result.Success)
                await RefreshStateAsync();
        });
    }

    private void OnPpmClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag)
            return;
        if (!int.TryParse(tag, out int lvl))
            return;

        PpmSlider.Value = lvl;
        HighlightPpm(lvl);
        PpmValueText.Text = $"PPM {lvl} · {LabelForPpm(lvl)}";
        _ppmDebounce.Stop();
        _ppmDebounce.Start();
    }

    private void OnPpmValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PpmValueText is null)
            return;
        int lvl = (int)Math.Round(e.NewValue);
        PpmValueText.Text = $"PPM {lvl} · {LabelForPpm(lvl)}";
    }

    private void OnPpmPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _ppmDebounce.Stop();
        _ppmDebounce.Start();
    }

    // ---------- 瓷砖自适应缩放 ----------

    /// <summary>窗口内容宽（DIP）分档：窄 → 标准 → 宽。图标/标签/最小高度按档缩放。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTileScale(e.NewSize.Width);

    private void UpdateTileScale(double widthDip)
    {
        // 分档阈值按内容宽度（窗口 470 时内容约 430）
        double iconSize, labelSize, minHeight, spacing;
        if (widthDip < 420)
        {
            iconSize = 18; labelSize = 12; minHeight = 72; spacing = 4;
        }
        else if (widthDip < 520)
        {
            iconSize = 20; labelSize = 12.5; minHeight = 84; spacing = 6;
        }
        else if (widthDip < 620)
        {
            iconSize = 24; labelSize = 13.5; minHeight = 96; spacing = 7;
        }
        else
        {
            iconSize = 28; labelSize = 14.5; minHeight = 110; spacing = 8;
        }

        foreach (var (icon, label) in new[]
        {
            (ModeIconSmart, ModeLabelSmart),
            (ModeIconHigh, ModeLabelHigh),
            (ModeIconBeast, ModeLabelBeast),
        })
        {
            icon.FontSize = iconSize;
            label.FontSize = labelSize;
            if (icon.Parent is StackPanel sp)
                sp.Spacing = spacing;
        }

        ModeTileGrid.MinHeight = minHeight;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
        => _ = RunBusyAsync(async () => await RefreshStateAsync());

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_triggers, _settings, ApplySettings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    /// <summary>设置窗口保存采样率后的回调：立即套用到轮询定时器。</summary>
    private void ApplySettings()
    {
        _tempTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.TempPollSeconds));
        _procTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ProcScanSeconds));
    }

    private void OnGpuFixClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        BtnGpuFix.IsEnabled = false;
        SetStatus("正在修复 GPU 锁频（NvAPI + nvidia-smi -rgc）…");

        _ = Task.Run(async () =>
        {
            var result = await GpuClockFix.RunAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                SetStatus(result.Ok
                    ? "GPU 动态 P 状态已恢复" + (result.Message.Contains("-rgc") ? "，时钟锁已重置" : "")
                    : "GPU 修复未完全成功（详见下方信息）");
                if (GpuFixInfoText.Child is TextBlock info)
                    info.Text = result.Message;
                GpuFixInfoText.Visibility = Visibility.Visible;
                BtnGpuFix.IsEnabled = true;
            });
        });
    }
}
