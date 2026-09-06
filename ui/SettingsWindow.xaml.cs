using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HonorHelper.Protocol;

namespace HonorHelper;

/// <summary>
/// 设置窗口：⏱️ 采样率 + 🎯 程序联动管理。
/// 所有数据（采样率、联动规则）都持久化在后台 service；本窗口通过命名管道读写，
/// 不再直接触碰硬件或本地配置文件。每个程序的独立管理页含「打开时」「退出时」两个方框。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ServiceClient _client;
    private List<TriggerDto> _triggers = new();
    private SettingsDto _settings = new(5);
    private readonly DispatcherTimer _saveDebounce = new();
    private bool _loading = true;
    private bool _disposed;
    private bool _shutdownRequested;

    private TriggerDto? _editing;
    private bool _detailLoading;    private bool _programListDirty = true;
    private readonly List<(Border Card, TappedEventHandler Tapped, PointerEventHandler Entered, PointerEventHandler Exited)> _cardHandlers = new();

    public SettingsWindow(ServiceClient client)
    {
        InitializeComponent();
        Title = "设置 — H-Helper";

        _client = client;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 780));

        _saveDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _saveDebounce.Tick += OnSaveDebounceTick;

        FillCombo(OpenCombo, ProgramTriggers.ModeActions);
        FillCombo(OpenTpCombo, ProgramTriggers.TouchpadActions);
        FillCombo(CloseCombo, ProgramTriggers.ModeActions);
        FillCombo(CloseTpCombo, ProgramTriggers.TouchpadActions);

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        var s = await _client.GetSettingsAsync();
        if (s is not null) _settings = s;
        var t = await _client.GetTriggersAsync();
        if (t is not null) _triggers = t;

        TempPollSlider.Value = Math.Clamp(_settings.TempPollSeconds, 1, 60);
        UpdateIntervalLabels();
        BuildProgramList();
        _loading = false;
    }

    private async void OnSaveDebounceTick(object? sender, object e)
    {
        _saveDebounce.Stop();
        if (!_disposed)
            await _client.SetSettingsAsync(_settings);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownRequested)
            return;
        args.Cancel = true;
        ShowWindow(GetWindowHandle(), SW_HIDE);
    }

    private void OnClosed(object sender, WindowEventArgs args)
        => ReleaseResources();

    public void Shutdown()
    {
        if (_disposed)
            return;
        _shutdownRequested = true;
        Close();
    }

    public void ShowReusable()
    {
        if (_disposed)
            return;
        ShowWindow(GetWindowHandle(), SW_SHOW);
        SetForegroundWindow(GetWindowHandle());
    }

    private IntPtr GetWindowHandle()
        => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void ReleaseResources()
    {
        if (_disposed)
            return;
        _disposed = true;
        _saveDebounce.Stop();
        _saveDebounce.Tick -= OnSaveDebounceTick;
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnClosed;
        ClearProgramCards();
        Content = null;
    }

    private static void FillCombo(ComboBox box, (string Id, string Label)[] actions)
    {
        foreach (var (id, label) in actions)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = id, FontSize = 12.5 });
    }

    // ---------- 采样率 ----------

    private void OnTempPollChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        _settings = _settings with { TempPollSeconds = (int)Math.Round(e.NewValue) };
        UpdateIntervalLabels();
        DebounceSave();
    }

    private void UpdateIntervalLabels()
    {
        TempPollValueText.Text = $"{_settings.TempPollSeconds} 秒";
    }

    private void DebounceSave()
    {
        if (_loading)
            return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    // ---------- 程序联动：列表视图 ----------

    private void SetStatus(string msg) => SettingsStatusText.Text = msg;

    private void ClearProgramCards()
    {
        foreach (var (card, tapped, entered, exited) in _cardHandlers)
        {
            card.Tapped -= tapped;
            card.PointerEntered -= entered;
            card.PointerExited -= exited;
        }
        _cardHandlers.Clear();
        ProgramList.Children.Clear();
    }

    private void BuildProgramList()
    {
        ClearProgramCards();

        if (_triggers.Count == 0)
        {
            ProgramList.Children.Add(new TextBlock
            {
                Text = "暂无程序。点右上角「添加程序」选择 exe，再点开它的卡片设置启动/退出行为。",
                FontSize = 11.5,
                Foreground = SolidColorBrush("#9A9AA2"),
                TextWrapping = TextWrapping.Wrap,
            });
            _programListDirty = false;
            return;
        }

        foreach (var t in _triggers)
        {
            var exeName = System.IO.Path.GetFileName(t.Path);

            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(exeName) ? t.Path : exeName,
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = SolidColorBrush(t.Enabled ? "#1A1A1A" : "#B9B9C0"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = t.Path,
                FontSize = 10.5,
                Foreground = SolidColorBrush("#9A9AA2"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = t.Enabled
                    ? $"打开：{SideSummary(t.OpenAction, t.OpenTouchpad)} · 退出：{SideSummary(t.CloseAction, t.CloseTouchpad)}"
                    : "已停用（点开可重新启用）",
                FontSize = 10.5,
                Foreground = SolidColorBrush(t.Enabled ? "#6A6A72" : "#C42B1C"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 18,
                Foreground = SolidColorBrush("#B9B9C0"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(info);
            grid.Children.Add(chevron);

            var card = new Border
            {
                Background = SolidColorBrush("#FFFFFF"),
                BorderBrush = SolidColorBrush("#E2E2E6"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Child = grid,
            };
            TappedEventHandler tapped = (_, _) => ShowDetail(t);
            PointerEventHandler entered = (_, _) => card.Background = SolidColorBrush("#F1F3F6");
            PointerEventHandler exited = (_, _) => card.Background = SolidColorBrush("#FFFFFF");
            card.Tapped += tapped;
            card.PointerEntered += entered;
            card.PointerExited += exited;
            _cardHandlers.Add((card, tapped, entered, exited));

            ProgramList.Children.Add(card);
        }
        _programListDirty = false;
    }

    // ---------- 程序联动：单程序管理页 ----------

    private void ShowDetail(TriggerDto t)
    {
        _editing = t;
        _detailLoading = true;

        DetailTitleText.Text = string.IsNullOrEmpty(System.IO.Path.GetFileName(t.Path))
            ? t.Path : System.IO.Path.GetFileName(t.Path);
        DetailPathText.Text = t.Path;
        EnabledToggle.IsOn = t.Enabled;

        SelectCombo(OpenCombo, ProgramTriggers.ModeActions, t.OpenAction);
        SelectCombo(OpenTpCombo, ProgramTriggers.TouchpadActions, t.OpenTouchpad);
        SelectCombo(CloseCombo, ProgramTriggers.ModeActions, t.CloseAction);
        SelectCombo(CloseTpCombo, ProgramTriggers.TouchpadActions, t.CloseTouchpad);
        GpuFixToggle.IsOn = t.GpuFixOnExit;

        bool openBeast = t.OpenAction == "beast";
        OpenPpmPanel.Visibility = openBeast ? Visibility.Visible : Visibility.Collapsed;
        if (openBeast)
        {
            OpenPpmSlider.Value = Math.Clamp(t.OpenPpm, 0, 4);
            OpenPpmValueText.Text = PpmText(t.OpenPpm);
        }

        CoreOffsetSlider.Value = Math.Clamp(t.GpuCoreMhz, CoreMin, CoreMax);
        MemOffsetSlider.Value = Math.Clamp(t.GpuMemMhz, MemMin, MemMax);
        UpdateOffsetLabels();
        _detailLoading = false;

        ListPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void ShowList()
    {
        _editing = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        if (_programListDirty)
            BuildProgramList();
    }

    /// <summary>
    /// TriggerDto 是 immutable record：with 只产生新对象，不会改列表里的旧项。
    /// 所有详情页编辑都走这里，把新对象写回 _triggers（按 Path 定位），再持久化。
    /// </summary>
    private void CommitEdit(TriggerDto updated)
    {
        _editing = updated;
        for (int i = 0; i < _triggers.Count; i++)
        {
            if (string.Equals(_triggers[i].Path, updated.Path, StringComparison.OrdinalIgnoreCase))
                _triggers[i] = updated;
        }
    }

    private static void SelectCombo(ComboBox box, (string Id, string Label)[] actions, string id)
    {
        for (int i = 0; i < actions.Length; i++)
        {
            if (actions[i].Id == id)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void OnOpenComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading || OpenCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        CommitEdit(_editing with { OpenAction = id });
        _programListDirty = true;
        _ = SaveTriggersAsync();

        bool beast = id == "beast";
        OpenPpmPanel.Visibility = beast ? Visibility.Visible : Visibility.Collapsed;
        if (beast)
        {
            _detailLoading = true;
            OpenPpmSlider.Value = Math.Clamp(_editing.OpenPpm, 0, 4);
            OpenPpmValueText.Text = PpmText(_editing.OpenPpm);
            _detailLoading = false;
        }
    }

    private void OnOpenPpmChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        int v = (int)Math.Round(e.NewValue);
        CommitEdit(_editing with { OpenPpm = v });
        OpenPpmValueText.Text = PpmText(v);
        _programListDirty = true;
        _ = SaveTriggersAsync();
    }

    private static string PpmText(int v)
        => $"{v} · {(PpmModes.PpmNames.TryGetValue(v, out var n) ? n : "Level " + v)}";

    private void OnOpenTpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading || OpenTpCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        CommitEdit(_editing with { OpenTouchpad = id });
        _programListDirty = true;
        _ = SaveTriggersAsync();
    }

    private void OnCloseComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading || CloseCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        CommitEdit(_editing with { CloseAction = id });
        _programListDirty = true;
        _ = SaveTriggersAsync();
    }

    private void OnCloseTpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading || CloseTpCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        CommitEdit(_editing with { CloseTouchpad = id });
        _programListDirty = true;
        _ = SaveTriggersAsync();
    }

    // ---------- 程序管理页：GPU 超频 ----------

    private void OnCoreOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        int v = (int)Math.Round(e.NewValue);
        CommitEdit(_editing with { GpuCoreMhz = v });
        _programListDirty = true;
        _ = SaveTriggersAsync();
        if (!_offsetBoxUpdating)
            CoreOffsetBox.Text = OffsetText(v);
    }

    private void OnMemOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        int v = (int)Math.Round(e.NewValue);
        CommitEdit(_editing with { GpuMemMhz = v });
        _programListDirty = true;
        _ = SaveTriggersAsync();
        if (!_offsetBoxUpdating)
            MemOffsetBox.Text = OffsetText(v);
    }

    private void UpdateOffsetLabels()
    {
        _offsetBoxUpdating = true;
        CoreOffsetBox.Text = OffsetText((int)Math.Round(CoreOffsetSlider.Value));
        MemOffsetBox.Text = OffsetText((int)Math.Round(MemOffsetSlider.Value));
        _offsetBoxUpdating = false;
    }

    private bool _offsetBoxUpdating;

    private const int CoreMin = -200, CoreMax = 400;
    private const int MemMin = -200, MemMax = 1000;

    private void OnCoreBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_offsetBoxUpdating || _editing is null || _detailLoading)
            return;
        if (!TryParseOffset(CoreOffsetBox.Text, out int v))
            return;
        v = Math.Clamp(v, CoreMin, CoreMax);
        _offsetBoxUpdating = true;
        CoreOffsetSlider.Value = v;
        _offsetBoxUpdating = false;
    }

    private void OnMemBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_offsetBoxUpdating || _editing is null || _detailLoading)
            return;
        if (!TryParseOffset(MemOffsetBox.Text, out int v))
            return;
        v = Math.Clamp(v, MemMin, MemMax);
        _offsetBoxUpdating = true;
        MemOffsetSlider.Value = v;
        _offsetBoxUpdating = false;
    }

    private void OnCoreBoxLostFocus(object sender, RoutedEventArgs e)
        => CoreOffsetBox.Text = OffsetText((int)Math.Round(CoreOffsetSlider.Value));

    private void OnMemBoxLostFocus(object sender, RoutedEventArgs e)
        => MemOffsetBox.Text = OffsetText((int)Math.Round(MemOffsetSlider.Value));

    private static bool TryParseOffset(string? text, out int value)
    {
        var s = (text ?? "").Replace("MHz", "").Replace("mhz", "").Trim();
        return int.TryParse(s, out value);
    }

    private static string OffsetText(int v) => $"{v:+0;-0;0} MHz";

    private void OnGpuFixExitToggled(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        CommitEdit(_editing with { GpuFixOnExit = GpuFixToggle.IsOn });
        _programListDirty = true;
        _ = SaveTriggersAsync();
    }

    private static string SideSummary(string mode, string touchpad)
    {
        var s = ProgramTriggers.ActionLabel(mode);
        if (touchpad is "tp_on" or "tp_off")
            s += "/" + ProgramTriggers.ActionLabel(touchpad);
        return s;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowList();

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        CommitEdit(_editing with { Enabled = EnabledToggle.IsOn });
        _programListDirty = true;
        _ = SaveTriggersAsync();
        SetStatus(EnabledToggle.IsOn ? "已启用联动" : "已停用联动");
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;
        var removed = _editing;
        _triggers.RemoveAll(x => string.Equals(x.Path, removed.Path, StringComparison.OrdinalIgnoreCase));
        _programListDirty = true;
        _ = SaveTriggersAsync();
        ShowList();
        SetStatus($"已删除 {System.IO.Path.GetFileName(removed.Path)} 的联动规则");
    }

    private async void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await Task.Run(PickExeViaPowerShell);
            if (string.IsNullOrEmpty(path))
                return;

            if (_triggers.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("该程序已在联动列表中");
                return;
            }

            _triggers.Add(new TriggerDto(path, "none", 2, "none", "none", "none", 0, 0, false, true));
            _programListDirty = true;
            await SaveTriggersAsync();
            SetStatus($"已添加 {System.IO.Path.GetFileName(path)}，点开卡片设置打开/退出行为");
            BuildProgramList();
        }
        catch (Exception ex)
        {
            SetStatus($"添加程序失败：{ex.Message}");
        }
    }

    private async Task SaveTriggersAsync()
    {
        var r = await _client.SaveTriggersAsync(_triggers);
        if (!r.Ok)
            SetStatus($"保存失败：{r.Message}");
        _programListDirty = true;
    }

    /// <summary>弹 PowerShell 子进程文件对话框。返回所选路径；用户取消返回 null。</summary>
    private static string? PickExeViaPowerShell()
    {
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "$d = New-Object System.Windows.Forms.OpenFileDialog; " +
            "$d.Filter = '程序 (*.exe)|*.exe|所有文件 (*.*)|*.*'; " +
            "$d.Title = '选择要联动的程序'; " +
            "if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($d.FileName))) }";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -STA -Command \"" + script + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null)
            return null;

        var outText = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (string.IsNullOrEmpty(outText))
            return null;

        try
        {
            var path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(outText));
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---------- helpers（与主窗口同款） ----------

    private static Windows.UI.Color WinColor(string hex)
    {
        if (hex.StartsWith('#'))
            hex = hex[1..];
        return Microsoft.UI.ColorHelper.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush SolidColorBrush(string hex)
        => new(WinColor(hex));
}
