using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HonorHelper.Protocol;

namespace HonorHelper;

public sealed partial class MainWindow : Window
{
    // G-Helper 风格浅色配色：选中描边（性能区绿色），其余白底细灰边
    private const string FgHex = "#1A1A1A";
    private const string TileBorderHex = "#D8D8DC";
    private const string SelectGreenHex = "#0FA05A";
    private const string AccentBlueHex = "#0F6CBD";

    private const int CollapsedHeight = 820;
    private const int ExpandedHeight = 1020;

    private readonly ServiceClient _client = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _ppmDebounce = new();
    private readonly DispatcherTimer _statusRevertTimer = new();
    private SettingsWindow? _settingsWindow;

    private bool _initializing = true;
    private string? _activeModeId;
    private bool _busy;
    private bool _ppmOpen;
    private bool _pollPending;
    private bool _touchpadSync;
    private bool _touchpadBusy;
    private bool _disposed;
    private string? _modeStatusText;
    private bool _serviceUp;
    private bool _haveSetModeStatus;
    private int _pollIntervalSeconds = 3;

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

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyCaptionButtonColors();
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = this.AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        }

        _ppmDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _ppmDebounce.Tick += OnPpmDebounceTick;

        _statusRevertTimer.Interval = TimeSpan.FromSeconds(4);
        _statusRevertTimer.Tick += OnStatusRevertTick;

        // 轮询：定期向 service 拉取完整硬件快照（温度/风扇/模式/触控板/GPU）。
        _pollTimer.Interval = TimeSpan.FromSeconds(3);
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();

        Root.SizeChanged += OnRootSizeChanged;
        UpdateTileScale(Root.ActualWidth);

        HighlightModes(null);
        HighlightPpm(-1);
        _ = RefreshStateAsync();
        _initializing = false;
    }

    // ---------- timers ----------

    private void OnPollTick(object? sender, object e)
        => _ = RefreshSnapshotAsync();

    private void OnPpmDebounceTick(object? sender, object e)
    {
        _ppmDebounce.Stop();
        _ = ApplyPpmFromSliderAsync();
    }

    private void OnStatusRevertTick(object? sender, object e)
    {
        _statusRevertTimer.Stop();
        if (_modeStatusText is not null)
            StatusText.Text = _modeStatusText;
    }

    // ---------- helpers ----------

    private PerfProfile? ProfileForId(string id)
        => PpmModes.Profiles.FirstOrDefault(pr => pr.Id == id);

    private Button[] ModeButtons => new[] { BtnModeSmart, BtnModeHigh, BtnModeBeast };
    private Button[] PpmButtons => new[] { BtnPpm0, BtnPpm1, BtnPpm2, BtnPpm3, BtnPpm4 };

    private static string ReadTag(Button b) => b.Tag as string ?? string.Empty;

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy)
            return;
        _busy = true;
        SetBusy(true);
        try { await work(); }
        finally { _busy = false; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var b in ModeButtons.Concat(PpmButtons).Concat(new[] { BtnRefresh }))
            b.IsEnabled = !busy;
        PpmSlider.IsEnabled = !busy;
        if (busy)
            SetStatus("正在切换…");
    }

    private void SetModeStatus(string text)
    {
        _modeStatusText = text;
        _statusRevertTimer.Stop();
        StatusText.Text = text;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _statusRevertTimer.Stop();
        _statusRevertTimer.Start();
    }

    private string ModeStatusText(int mode)
        => $"当前：perf {mode} · {(PpmModes.PerfModeNames.TryGetValue(mode, out var n) ? n : "未知")}";

    private void UpdateModeStatus(int mode, bool force)
    {
        _modeStatusText = ModeStatusText(mode);
        if (force)
        {
            _statusRevertTimer.Stop();
            StatusText.Text = _modeStatusText;
        }
        else if (StatusText.Text?.StartsWith("当前：", System.StringComparison.Ordinal) == true)
        {
            StatusText.Text = _modeStatusText;
        }
    }

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
            var b = PpmButtons[i];
            b.BorderBrush = SolidColorBrush(on ? AccentBlueHex : TileBorderHex);
            b.BorderThickness = new Thickness(on ? 2 : 1);
            b.Foreground = SolidColorBrush(on ? AccentBlueHex : "#70707A");
            b.FontWeight = on
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private static Windows.UI.Color WinColor(string hex)
    {
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6)
            throw new FormatException($"Bad color: {hex}");
        return Microsoft.UI.ColorHelper.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush SolidColorBrush(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(WinColor(hex));
            BrushCache[hex] = brush;
        }
        return brush;
    }

    private void ApplyCaptionButtonColors()
    {
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

    // ---------- service connection / snapshot ----------

    private async Task RefreshStateAsync()
    {
        _serviceUp = ServiceClient.IsServiceRunning();

        if (!_serviceUp)
        {
            PerfModeRun.Text = "—";
            AdminText.Text = "后台服务未运行：点「设置」安装/启动后台服务（需管理员一次性提权）。";
            AdminText.Visibility = Visibility.Visible;
            SetModeStatus("后台服务未运行，无法读取硬件。");
            return;
        }

        AdminText.Visibility = Visibility.Collapsed;
        await RefreshSnapshotAsync();
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_pollPending)
            return;
        _pollPending = true;
        try
        {
            // 温度/风扇采样间隔由后台服务的 TempPollSeconds 决定：每次轮询顺带
            // 同步一次，用户在设置里改了间隔即可生效（值变了才重置定时器，避免抖动）。
            await SyncPollIntervalAsync();

            var snap = await _client.GetSnapshotAsync();
            if (snap is null)
                return;

            // temps
            var cpuT = snap.Temps.FirstOrDefault(t => t.Name == "CPU");
            var gpuT = snap.Temps.FirstOrDefault(t => t.Name == "GPU");
            SetTempRun(TempRun, "CPU", cpuT is null ? 0 : cpuT.DegreeC, snap.CpuFreqMhz);
            SetTempRun(TempGpuRun, "GPU", gpuT is null ? 0 : gpuT.DegreeC, snap.Gpu?.ClockMhz);

            IterateTempsGrid(snap);

            SetFanRuns(snap.Fans);

            UpdateTouchpadUI(snap.Touchpad);

            if (snap.Mode >= 0)
            {
                ApplyPerfModeToUi(snap.Mode);
                // Force the mode status on the first successful snapshot so the initial
                // "正在获取当前模式…" placeholder is replaced, then use the poll path.
                UpdateModeStatus(snap.Mode, force: _haveSetModeStatus ? false : true);
                _haveSetModeStatus = true;
            }

            // PPM mirror: EC has no readback, the service reports the last level
            // it wrote (trigger/slider/profile). Adopt it when the UI disagrees,
            // so a trigger-fired PPM shows up on the slider without a click.
            if (snap.LastPpm is >= 0 and <= 4 && snap.Mode == PpmModes.BeastPerfMode)
            {
                int lvl = snap.LastPpm.Value;
                if (lvl != CurrentPpm && !_ppmDebounce.IsEnabled)
                {
                    PpmSlider.Value = lvl;
                    PpmValueText.Text = $"PPM {lvl}";
                    if (_ppmOpen)
                        HighlightPpm(lvl);
                }
            }

            _lastTempRefreshUtc = System.DateTime.UtcNow;
            TempsUpdatedText.Text = _lastTempRefreshUtc.ToLocalTime().ToString("HH:mm:ss");
        }
        catch
        {
            // poll failure: keep last state
        }
        finally
        {
            _pollPending = false;
        }
    }

    /// <summary>
    /// 从后台服务读取温度/风扇采样间隔（TempPollSeconds），把它应用到 UI 的轮询定时器。
    /// 仅在值变化时重置定时器，避免频繁重启造成抖动。
    /// </summary>
    private async Task SyncPollIntervalAsync()
    {
        try
        {
            var s = await _client.GetSettingsAsync();
            if (s is null)
                return;
            int secs = Math.Max(1, s.TempPollSeconds);
            if (secs == _pollIntervalSeconds)
                return;
            _pollIntervalSeconds = secs;
            _pollTimer.Interval = TimeSpan.FromSeconds(secs);
        }
        catch
        {
            // 读设置失败不影响本轮轮询
        }
    }

    private void ApplyPerfModeToUi(int mode, bool force = false)
    {
        if (mode < 0)
        {
            PerfModeRun.Text = "—";
            _lastAppliedMode = mode;
            return;
        }

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

    private int CurrentPpm => (int)System.Math.Round(PpmSlider.Value);

    private async Task ApplyPpmFromSliderAsync()
    {
        int lvl = CurrentPpm;
        HighlightPpm(lvl);
        try
        {
            var r = await _client.SetPpmAsync(lvl);
            SetStatus(r.Ok ? $"PPM 已设为 {lvl}" : $"PPM {lvl} 设置失败");
        }
        catch (System.Exception ex)
        {
            SetStatus($"PPM {lvl} 设置失败：{ex.Message}");
        }
    }

    private static string LabelForPpm(int lvl)
        => PpmModes.PpmNames.TryGetValue(lvl, out var n) ? n : "未知";

    // ---------- 温度 ----------

    private const int TempHotC = 80;

    private string TempColorHex(int t)
        => t >= TempHotC ? "#D13438" : t >= 65 ? "#B25E09" : "#3A3A40";

    private void SetTempRun(Microsoft.UI.Xaml.Documents.Run run, string name, int? t, int? freqMhz = null)
    {
        string text = t is null
            ? ""
            : $"{name}: {t}°C{(freqMhz is > 0 ? $" {freqMhz / 1000.0:0.#}GHz" : "")}  ";
        if (run.Text == text)
            return;
        run.Text = text;
        if (t is not null)
            run.Foreground = SolidColorBrush(TempColorHex(t.Value));
    }

    private readonly List<Microsoft.UI.Xaml.Documents.Run> _tempCellRuns = new();
    private System.DateTime _lastTempRefreshUtc;

    private void BuildTempsGrid()
    {
        if (_tempCellRuns.Count > 0 || TempsGrid is null)
            return;

        for (int i = 0; i < CoreTables.TempChannels.Count; i++)
        {
            var ch = CoreTables.TempChannels[i];
            var sp = new StackPanel { Spacing = 2 };
            Grid.SetColumn(sp, i % 4);
            Grid.SetRow(sp, i / 4);
            var name = new TextBlock { Text = ch.Name, FontSize = 11, Foreground = SolidColorBrush("#8A8A92") };
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = "—" };
            var val = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            val.Inlines.Add(run);
            sp.Children.Add(name);
            sp.Children.Add(val);
            TempsGrid.Children.Add(sp);
            _tempCellRuns.Add(run);
        }

        while (TempsGrid.RowDefinitions.Count < (CoreTables.TempChannels.Count + 3) / 4)
            TempsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    }

    private void IterateTempsGrid(StatusSnapshot snap)
    {
        BuildTempsGrid();
        while (_lastTemps.Count < _tempCellRuns.Count)
            _lastTemps.Add(int.MinValue);
        for (int i = 0; i < CoreTables.TempChannels.Count && i < _tempCellRuns.Count; i++)
        {
            var ch = CoreTables.TempChannels[i];
            var reading = snap.Temps.FirstOrDefault(t => t.Name == ch.Name);
            bool ok = reading is not null && reading.DegreeC > 0;
            int val = ok ? reading!.DegreeC : -1;
            if (_lastTemps[i] == val)
                continue;
            _lastTemps[i] = val;
            _tempCellRuns[i].Text = ok ? $"{reading!.DegreeC}°C" : "—";
            _tempCellRuns[i].Foreground = SolidColorBrush(ok ? TempColorHex(reading!.DegreeC) : "#B9B9C0");
        }
    }

    private Microsoft.UI.Xaml.Documents.Run[] FanRuns => new[] { FanCpuRun, FanGpuRun, Fan3Run };

    private void SetFanRuns(IReadOnlyList<FanReading> fans)
    {
        var runs = FanRuns;
        for (int i = 0; i < CoreTables.Fans.Length && i < runs.Length; i++)
        {
            var (id, name) = CoreTables.Fans[i];
            var run = runs[i];
            var f = fans.FirstOrDefault(x => x.Id == id);
            string text = f is not null ? $"{name} {f.Rpm} rpm   " : "";
            if (run.Text == text)
                continue;
            run.Text = text;
        }

        var limits = new List<string>();
        var stopped = new List<string>();
        foreach (var (id, name) in CoreTables.Fans)
        {
            var f = fans.FirstOrDefault(x => x.Id == id);
            if (f is null)
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

    // ---------- 触控板 ----------

    private void UpdateTouchpadUI(int? state)
    {
        if (state is not null && state == _lastTouchpadState)
            return;
        _lastTouchpadState = state;

        if (state is null)
        {
            _touchpadSync = true;
            TouchpadToggle.IsOn = false;
            _touchpadSync = false;
            TouchpadToggle.IsEnabled = false;
            TouchpadNoteText.Text = "读取失败";
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
        if (_touchpadSync || _touchpadBusy)
            return;
        bool on = TouchpadToggle.IsOn;

        _touchpadBusy = true;
        TouchpadToggle.IsEnabled = false;
        SetStatus(on ? "正在开启触控板…" : "正在关闭触控板…");
        try
        {
            var r = await _client.SetTouchpadAsync(on);
            SetStatus(r.Ok ? (on ? "触控板已开启" : "触控板已关闭") : "触控板设置失败");
        }
        catch (System.Exception ex)
        {
            SetStatus($"触控板设置失败：{ex.Message}");
        }
        finally
        {
            _touchpadBusy = false;
            UpdateTouchpadUI(await _client.GetSnapshotAsync() is { } s ? s.Touchpad : null);
        }
    }

    // ---------- lifetime ----------

    public void Shutdown()
    {
        if (_disposed)
            return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _ppmDebounce.Stop();
        _ppmDebounce.Tick -= OnPpmDebounceTick;
        _statusRevertTimer.Stop();
        _statusRevertTimer.Tick -= OnStatusRevertTick;
        Root.SizeChanged -= OnRootSizeChanged;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Shutdown();
            _settingsWindow = null;
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

        _activeModeId = id;
        HighlightModes(id);
        if (profile.PerfMode == PpmModes.BeastPerfMode)
            ShowPpmPanel();
        else
            HidePpmPanel();

        int? ppm = profile.PerfMode == PpmModes.BeastPerfMode ? CurrentPpm : null;

        _ = RunBusyAsync(async () =>
        {
            var result = await _client.ApplyProfileAsync(profile.Id, ppm);
            SetStatus(result.Message);
            if (result.Ok)
                await RefreshSnapshotAsync();
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
        PpmValueText.Text = $"PPM {lvl}";
        _ppmDebounce.Stop();
        _ppmDebounce.Start();
    }

    private void OnPpmValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PpmValueText is null)
            return;
        int lvl = (int)System.Math.Round(e.NewValue);
        PpmValueText.Text = $"PPM {lvl}";
    }

    private void OnPpmPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _ppmDebounce.Stop();
        _ppmDebounce.Start();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTileScale(e.NewSize.Width);

    private void UpdateTileScale(double widthDip)
    {
        double iconSize, labelSize, minHeight, spacing;
        if (widthDip < 420) { iconSize = 18; labelSize = 12; minHeight = 72; spacing = 4; }
        else if (widthDip < 520) { iconSize = 20; labelSize = 12.5; minHeight = 84; spacing = 6; }
        else if (widthDip < 620) { iconSize = 24; labelSize = 13.5; minHeight = 96; spacing = 7; }
        else { iconSize = 28; labelSize = 14.5; minHeight = 110; spacing = 8; }

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
            _settingsWindow = new SettingsWindow(_client);
        _settingsWindow.ShowReusable();
    }

    private void OnGpuFixClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        BtnGpuFix.IsEnabled = false;
        SetStatus("正在修复 GPU 锁频（NvAPI + nvidia-smi -rgc）…");

        _ = Task.Run(async () =>
        {
            var result = await _client.RunGpuFixAsync(skipClockReset: false);
            DispatcherQueue.TryEnqueue(() =>
            {
                bool ok = result?.NvapiStatus == 0;
                SetStatus(ok ? "GPU 动态 P 状态已恢复" : "GPU 修复未完全成功（详见下方信息）");
                if (GpuFixInfoText.Child is TextBlock info)
                    info.Text = result?.Log ?? "";
                GpuFixInfoText.Visibility = Visibility.Visible;
                BtnGpuFix.IsEnabled = true;
            });
        });
    }
}
