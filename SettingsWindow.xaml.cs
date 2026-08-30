using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HonorHelper;

/// <summary>
/// 设置窗口：⏱️ 采样率 + 🎯 程序联动管理。
/// 每个程序的独立管理页含「打开时」「退出时」两个方框，各自从下拉选择动作
/// （无操作 / 切换性能模式 / 开关触控板）；退出框另带该程序专属的「自动 GPU 锁频修复」开关。
/// 与主窗口共享同一份 _triggers 列表和 AppSettings，改动即时保存；
/// 采样率变化通过 onSettingsChanged 回调通知主窗口套用轮询定时器。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly List<ProgramTrigger> _triggers;
    private readonly AppSettings _settings;
    private readonly Action _onSettingsChanged;
    private readonly DispatcherTimer _saveDebounce = new();
    private bool _loading = true;

    /// <summary>当前在管理页编辑的规则；null 表示处于列表视图。</summary>
    private ProgramTrigger? _editing;
    private bool _detailLoading;

    public SettingsWindow(List<ProgramTrigger> triggers, AppSettings settings, Action onSettingsChanged)
    {
        InitializeComponent();
        Title = "设置 — H-Helper";

        _triggers = triggers;
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 780));

        _saveDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SettingsStore.Save(_settings);
            _onSettingsChanged();
        };

        // 四个动作下拉：打开/退出 × 性能模式/触控板
        FillCombo(OpenCombo, ProgramTriggers.ModeActions);
        FillCombo(OpenTpCombo, ProgramTriggers.TouchpadActions);
        FillCombo(CloseCombo, ProgramTriggers.ModeActions);
        FillCombo(CloseTpCombo, ProgramTriggers.TouchpadActions);

        TempPollSlider.Value = Math.Clamp(_settings.TempPollSeconds, 1, 60);
        ProcScanSlider.Value = Math.Clamp(_settings.ProcScanSeconds, 1, 30);
        UpdateIntervalLabels();
        _loading = false;

        BuildProgramList();
    }

    private static void FillCombo(ComboBox box, (string Id, string Label)[] actions)
    {
        foreach (var (id, label) in actions)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = id, FontSize = 12.5 });
    }

    // ---------- 采样率 ----------

    private void OnTempPollChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // XAML 解析阶段赋 Minimum/Value 也会触发本事件，此时 _settings 还未注入，直接忽略
        if (_loading)
            return;
        _settings.TempPollSeconds = (int)Math.Round(e.NewValue);
        UpdateIntervalLabels();
        DebounceSave();
    }

    private void OnProcScanChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        _settings.ProcScanSeconds = (int)Math.Round(e.NewValue);
        UpdateIntervalLabels();
        DebounceSave();
    }

    private void UpdateIntervalLabels()
    {
        TempPollValueText.Text = $"{_settings.TempPollSeconds} 秒";
        ProcScanValueText.Text = $"{_settings.ProcScanSeconds} 秒";
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

    /// <summary>程序联动列表：按 _triggers 重建行（可点击卡片：名称/路径/行为摘要，点击进入管理页）。</summary>
    private void BuildProgramList()
    {
        ProgramList.Children.Clear();

        if (_triggers.Count == 0)
        {
            ProgramList.Children.Add(new TextBlock
            {
                Text = "暂无程序。点右上角「添加程序」选择 exe，再点开它的卡片设置启动/退出行为。",
                FontSize = 11.5,
                Foreground = SolidColorBrush("#9A9AA2"),
                TextWrapping = TextWrapping.Wrap,
            });
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
            card.Tapped += (_, _) => ShowDetail(t);
            // 悬停变色，提示可点击
            card.PointerEntered += (_, _) => card.Background = SolidColorBrush("#F1F3F6");
            card.PointerExited += (_, _) => card.Background = SolidColorBrush("#FFFFFF");

            ProgramList.Children.Add(card);
        }
    }

    // ---------- 程序联动：单程序管理页 ----------

    private void ShowDetail(ProgramTrigger t)
    {
        _editing = t;
        _detailLoading = true;

        var exeName = System.IO.Path.GetFileName(t.Path);
        DetailTitleText.Text = string.IsNullOrEmpty(exeName) ? t.Path : exeName;
        DetailPathText.Text = t.Path;
        EnabledToggle.IsOn = t.Enabled;

        SelectCombo(OpenCombo, ProgramTriggers.ModeActions, t.OpenAction);
        SelectCombo(OpenTpCombo, ProgramTriggers.TouchpadActions, t.OpenTouchpad);
        SelectCombo(CloseCombo, ProgramTriggers.ModeActions, t.CloseAction);
        SelectCombo(CloseTpCombo, ProgramTriggers.TouchpadActions, t.CloseTouchpad);
        GpuFixToggle.IsOn = t.GpuFixOnExit;

        // PPM 滑条：仅狂战模式显示
        bool openBeast = t.OpenAction == "beast";
        OpenPpmPanel.Visibility = openBeast ? Visibility.Visible : Visibility.Collapsed;
        if (openBeast)
        {
            OpenPpmSlider.Value = Math.Clamp(t.OpenPpm, 0, 4);
            OpenPpmValueText.Text = PpmText(t.OpenPpm);
        }

        _detailLoading = true;
        CoreOffsetSlider.Value = Math.Clamp(t.GpuCoreMhz, CoreMin, CoreMax);
        MemOffsetSlider.Value = Math.Clamp(t.GpuMemMhz, MemMin, MemMax);
        UpdateOffsetLabels();
        _detailLoading = false;

        ListPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        _detailLoading = false;
    }

    private void ShowList()
    {
        _editing = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        BuildProgramList();
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
        if (_editing is null || _detailLoading ||
            OpenCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        _editing.OpenAction = id;
        ProgramTriggers.Save(_triggers);

        // 狂战模式显示 PPM 滑条（PPM 只在狂战模式下可写）
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
        _editing.OpenPpm = v;
        OpenPpmValueText.Text = PpmText(v);
        ProgramTriggers.Save(_triggers);
    }

    private static string PpmText(int v)
        => $"{v} · {(PpmModes.PpmNames.TryGetValue(v, out var n) ? n : "Level " + v)}";

    private void OnOpenTpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading ||
            OpenTpCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        _editing.OpenTouchpad = id;
        ProgramTriggers.Save(_triggers);
    }

    private void OnCloseComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading ||
            CloseCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        _editing.CloseAction = id;
        ProgramTriggers.Save(_triggers);
    }

    private void OnCloseTpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing is null || _detailLoading ||
            CloseTpCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string id)
            return;
        _editing.CloseTouchpad = id;
        ProgramTriggers.Save(_triggers);
    }

    // ---------- 程序管理页：GPU 超频（NVAPI SetPstates20） ----------

    private void OnCoreOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        int v = (int)Math.Round(e.NewValue);
        _editing.GpuCoreMhz = v;
        ProgramTriggers.Save(_triggers);
        if (!_offsetBoxUpdating)
            CoreOffsetBox.Text = OffsetText(v);
    }

    private void OnMemOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editing is null || _detailLoading)
            return;
        int v = (int)Math.Round(e.NewValue);
        _editing.GpuMemMhz = v;
        ProgramTriggers.Save(_triggers);
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

    // ---------- 管理页：超频数值输入框（与滑条双向联动） ----------

    private bool _offsetBoxUpdating;

    // 偏移限值：核心 -200..+400，显存 -200..+1000（超出范围输入会被钳制）
    private const int CoreMin = -200, CoreMax = 400;
    private const int MemMin = -200, MemMax = 1000;

    private void OnCoreBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_offsetBoxUpdating || _editing is null || _detailLoading)
            return;
        if (!TryParseOffset(CoreOffsetBox.Text, out int v))
            return;   // 输入中（如只打了负号），失焦时再归一
        v = Math.Clamp(v, CoreMin, CoreMax);
        _offsetBoxUpdating = true;
        CoreOffsetSlider.Value = v;   // 触发 OnCoreOffsetChanged：保存 + 更新摘要
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

    /// <summary>解析输入的偏移值（容忍 "+25 MHz" 等格式）。解析失败返回 false。</summary>
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
        _editing.GpuFixOnExit = GpuFixToggle.IsOn;
        ProgramTriggers.Save(_triggers);
    }

    /// <summary>列表摘要的单侧文本：性能模式 + 可选触控板动作（如"狂战/关闭触控板"）。</summary>
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
        _editing.Enabled = EnabledToggle.IsOn;
        ProgramTriggers.Save(_triggers);
        SetStatus(EnabledToggle.IsOn ? "已启用联动" : "已停用联动");
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;
        var removed = _editing;
        _triggers.Remove(removed);
        ProgramTriggers.Save(_triggers);
        ShowList();
        SetStatus($"已删除 {System.IO.Path.GetFileName(removed.Path)} 的联动规则");
    }

    private async void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // WinUI 的 FileOpenPicker 在管理员进程里被 broker 拒绝（COMException），
            // comdlg32 手写结构体又对 lStructSize 校验极其敏感。所以改为在隐藏的
            // PowerShell 子进程里弹 WinForms 文件对话框（PS 5.1 自带 WinForms，
            // 子进程继承管理员权限），选择结果以 Base64 回传，完全绕开上述限制。
            var path = await Task.Run(PickExeViaPowerShell);
            if (string.IsNullOrEmpty(path))
                return;

            if (_triggers.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("该程序已在联动列表中");
                return;
            }

            var added = new ProgramTrigger { Path = path };
            _triggers.Add(added);
            ProgramTriggers.Save(_triggers);
            SetStatus($"已添加 {System.IO.Path.GetFileName(path)}，点开卡片设置打开/退出行为");
            BuildProgramList();
        }
        catch (Exception ex)
        {
            SetStatus($"添加程序失败：{ex.Message}");
        }
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
            return null; // 用户取消

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
        return Microsoft.UI.ColorHelper.FromArgb(
            255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush SolidColorBrush(string hex)
        => new(WinColor(hex));
}
