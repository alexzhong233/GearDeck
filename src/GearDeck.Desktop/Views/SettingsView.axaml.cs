using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Services;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Views;

public partial class SettingsView : UserControl
{
    // NumberBox / ToggleSwitch 的事件在 LoadSettings() 赋值期间必须被抑制。
    private bool _binding = true;
    private bool _checking;
    private bool _downloading;
    private bool _syncing;

    public SettingsView()
    {
        _binding = true;
        InitializeComponent();
        CardsGrid.SizeChanged += (_, _) => UpdateResponsiveColumns();
        WireEvents();
        LoadSettings();
        _binding = false;
    }

    private bool _twoCol = true;

    private void UpdateResponsiveColumns()
    {
        double w = CardsGrid.Bounds.Width;
        if (w <= 1) return;
        bool wantTwo = w >= 820;
        if (wantTwo == _twoCol) return;
        _twoCol = wantTwo;
        if (wantTwo)
        {
            CardsGrid.ColumnDefinitions = new ColumnDefinitions("*,14,*");
            Grid.SetRow(RightColumnPanel, 0);
            Grid.SetColumn(RightColumnPanel, 2);
            RightColumnPanel.Margin = new Thickness(0);
        }
        else
        {
            CardsGrid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetRow(RightColumnPanel, 1);
            Grid.SetColumn(RightColumnPanel, 0);
            RightColumnPanel.Margin = new Thickness(0, 14, 0, 0);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (App.Updater == null) return;
        App.Updater.StateChanged -= OnUpdateStateChanged;
        App.Updater.StateChanged += OnUpdateStateChanged;
        RenderUpdateOutcome(App.Updater.LastOutcome);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (App.Updater != null)
            App.Updater.StateChanged -= OnUpdateStateChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnUpdateStateChanged() => RenderUpdateOutcome(App.Updater.LastOutcome);

    private void WireEvents()
    {
        // Value 与 IsChecked 在 LoadSettings() 中用代码写入，避免 AXAML 内联 Value 与变更事件冲突。
        PollIntervalBox.ValueChanged += (_, _) => MarkUnsaved();
        GamePollIntervalBox.ValueChanged += (_, _) => MarkUnsaved();
        LowBatteryThresholdBox.ValueChanged += (_, _) => MarkUnsaved();

        CloseToTraySwitch.IsCheckedChanged += (_, _) => ApplySimpleToggle(s => s.CloseToTray = CloseToTraySwitch.IsChecked == true);
        LaunchMinimizedSwitch.IsCheckedChanged += (_, _) => ApplySimpleToggle(s => s.LaunchMinimizedToTray = LaunchMinimizedSwitch.IsChecked == true);
        NotifySwitch.IsCheckedChanged += (_, _) => ApplySimpleToggle(s => s.NotifyOnProfileSwitch = NotifySwitch.IsChecked == true);
        GlobalLowBatNotifySwitch.IsCheckedChanged += (_, _) => ApplySimpleToggle(s => s.GlobalLowBatteryNotificationEnabled = GlobalLowBatNotifySwitch.IsChecked == true);
        AutoCheckUpdateSwitch.IsCheckedChanged += (_, _) => ApplySimpleToggle(s => s.CheckUpdateOnStartup = AutoCheckUpdateSwitch.IsChecked == true);

        AutoYieldSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            bool value = AutoYieldSwitch.IsChecked == true;
            App.Settings.Current.AutoYieldToVendorSoftware = value;
            ExternalConfiguratorGuard.AutoYieldEnabled = value;
            App.Settings.Save();
            MarkUnsaved();
        };

        DebugSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_binding) return;
            bool debug = DebugSwitch.IsChecked == true;
            App.Settings.Current.DebugModeEnabled = debug;
            GearDeck.Core.Diag.VerboseEnabled = debug;
            App.Settings.Save();
            MainWindow.Instance?.UpdateDebugVisibility();
            MarkUnsaved();
        };

        StartupSwitch.IsCheckedChanged += (_, _) => ApplyStartupToggle();

        CloudManifestUrlBox.TextChanged += (_, _) => MarkUnsaved();

        MirrorChannelCombo.SelectionChanged += (_, _) =>
        {
            if (_binding || MirrorChannelCombo.SelectedItem is not ComboBoxItem item) return;
            App.Settings.Current.GitHubMirrorUrl = item.Tag as string ?? "";
            App.Settings.Save();
            UpdateStatusText.Text = $"已切换更新通道：{item.Content}";
        };

        CheckUpdateBtn.Click += OnCheckUpdateClick;
        DownloadUpdateBtn.Click += OnDownloadUpdateClick;
        OpenReleasesBtn.Click += OnOpenReleasesClick;
        SyncCloudImagesBtn.Click += OnSyncCloudImagesClick;
        SaveBtn.Click += OnSaveClick;
    }

    private void LoadSettings()
    {
        _binding = true;
        SetNumberBox(PollIntervalBox, 5, 300, Math.Clamp(App.Settings.Current.PollIntervalSeconds, 5, 300));
        SetNumberBox(GamePollIntervalBox, 250, 5000, Math.Clamp(App.Settings.Current.GameAutoPollIntervalMs, 250, 5000));
        SetNumberBox(LowBatteryThresholdBox, 5, 80, Math.Clamp(App.Settings.Current.LowBatteryThreshold, 5, 80));
        CloseToTraySwitch.IsChecked = App.Settings.Current.CloseToTray;
        LaunchMinimizedSwitch.IsChecked = App.Settings.Current.LaunchMinimizedToTray;
        StartupSwitch.IsChecked = App.Settings.Current.LaunchOnStartup;
        NotifySwitch.IsChecked = App.Settings.Current.NotifyOnProfileSwitch;
        GlobalLowBatNotifySwitch.IsChecked = App.Settings.Current.GlobalLowBatteryNotificationEnabled;
        AutoYieldSwitch.IsChecked = App.Settings.Current.AutoYieldToVendorSoftware;
        DebugSwitch.IsChecked = App.Settings.Current.DebugModeEnabled;
        AutoCheckUpdateSwitch.IsChecked = App.Settings.Current.CheckUpdateOnStartup;
        PopulateMirrorChannels(App.Settings.Current.GitHubMirrorUrl ?? "");
        CloudManifestUrlBox.Text = App.Settings.Current.CustomCloudGalleryManifestUrl ?? "";
        _binding = false;

        UpdateStatusText.Text = $"当前版本 v{AppUpdateService.CurrentVersion}";
        StartupHintText.Text = StartupStateHint(App.Settings.Current.LaunchOnStartup, "", withDetail: false);
        SavedHintText.Text = "";
    }

    private void PopulateMirrorChannels(string currentUrl)
    {
        string normalized = currentUrl.Trim();
        var presets = new List<(string Label, string Url)>
        {
            ("GitHub 官方直连", ""),
            ("国内加速通道 1（ghfast）", "https://ghfast.top/"),
            ("国内加速通道 2（gh-proxy）", "https://gh-proxy.com/"),
        };

        if (!string.IsNullOrEmpty(normalized) &&
            !presets.Any(p => string.Equals(p.Url.TrimEnd('/'), normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            presets.Add(("当前自定义加速通道", normalized));
        }

        MirrorChannelCombo.Items.Clear();
        int selected = 0;
        for (int i = 0; i < presets.Count; i++)
        {
            var (label, url) = presets[i];
            MirrorChannelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = url });
            if (string.Equals(url.TrimEnd('/'), normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                selected = i;
        }
        MirrorChannelCombo.SelectedIndex = selected;
    }

    private string GetSelectedMirrorUrl()
        => (MirrorChannelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void MarkUnsaved()
    {
        if (!_binding)
            SavedHintText.Text = "有未保存的更改，请点击「保存设置」。";
    }

    private void ApplySimpleToggle(Action<AppSettings> write)
    {
        if (_binding) return;
        write(App.Settings.Current);
        App.Settings.Save();
        MarkUnsaved();
    }

    /// <summary>开机自启：写入 Registry(Win) / LaunchAgent(macOS)，写入失败时回滚开关。</summary>
    private void ApplyStartupToggle()
    {
        if (_binding) return;
        bool enable = StartupSwitch.IsChecked == true;
        // ApplyStartupRegistration 是 SettingsService 的静态入口（App.Settings 即该服务实例）。
        var (ok, detail) = SettingsService.ApplyStartupRegistration(enable);
        if (!ok)
        {
            _binding = true;
            StartupSwitch.IsChecked = !enable;
            _binding = false;
            StartupHintText.Text = $"设置开机自启失败：{detail}";
            return;
        }

        App.Settings.Current.LaunchOnStartup = enable;
        App.Settings.Save();
        StartupHintText.Text = StartupStateHint(enable, detail, withDetail: true);
        MarkUnsaved();
    }

    private static string StartupStateHint(bool enable, string detail, bool withDetail)
    {
        string platform = OperatingSystem.IsWindows()
            ? "Windows 启动项"
            : OperatingSystem.IsMacOS() ? "macOS LaunchAgent" : "系统自启动配置";
        string suffix = withDetail && !string.IsNullOrWhiteSpace(detail) ? $"（{detail}）" : "";
        return $"{(enable ? "已开启" : "已关闭")}开机自启 · {platform}{suffix} ({DateTime.Now:HH:mm:ss})";
    }

    private static void SetNumberBox(NumberBox box, double min, double max, double value)
    {
        box.Maximum = max;
        box.Minimum = min;
        box.Value = Math.Clamp(value, min, max);
    }

    /// <summary>FluentAvalonia NumberBox.Value 判空：null 或 NaN 均视为未填写。</summary>
    private static double ReadNumberBox(NumberBox box, double fallback)
    {
        double? raw = box.Value;
        return raw == null || double.IsNaN(raw.Value) ? fallback : raw.Value;
    }

    private static string NormalizeRepoSlug(string? text)
        => string.IsNullOrWhiteSpace(text) ? UpdateClient.DefaultRepoSlug : text.Trim();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            int hidSeconds = (int)Math.Round(Math.Clamp(ReadNumberBox(PollIntervalBox, 15), 5, 300));
            int gameMs = (int)Math.Round(Math.Clamp(ReadNumberBox(GamePollIntervalBox, 1000), 250, 5000));
            int lowBat = (int)Math.Round(Math.Clamp(ReadNumberBox(LowBatteryThresholdBox, 20), 5, 80));

            var s = App.Settings.Current;
            s.PollIntervalSeconds = hidSeconds;
            s.GameAutoPollIntervalMs = gameMs;
            s.LowBatteryThreshold = lowBat;
            s.CloseToTray = CloseToTraySwitch.IsChecked == true;
            s.LaunchMinimizedToTray = LaunchMinimizedSwitch.IsChecked == true;
            s.LaunchOnStartup = StartupSwitch.IsChecked == true;
            s.NotifyOnProfileSwitch = NotifySwitch.IsChecked == true;
            s.GlobalLowBatteryNotificationEnabled = GlobalLowBatNotifySwitch.IsChecked == true;
            s.AutoYieldToVendorSoftware = AutoYieldSwitch.IsChecked == true;
            s.DebugModeEnabled = DebugSwitch.IsChecked == true;
            GearDeck.Core.Diag.VerboseEnabled = s.DebugModeEnabled;
            s.CheckUpdateOnStartup = AutoCheckUpdateSwitch.IsChecked == true;
            s.GitHubMirrorUrl = GetSelectedMirrorUrl();
            s.UpdateRepoSlug = NormalizeRepoSlug(s.UpdateRepoSlug);
            s.CustomCloudGalleryManifestUrl = CloudManifestUrlBox.Text?.Trim() ?? "";
            App.Settings.Save();

            ExternalConfiguratorGuard.AutoYieldEnabled = s.AutoYieldToVendorSoftware;
            var (startupOk, startupDetail) = SettingsService.ApplyStartupRegistration(s.LaunchOnStartup);
            MainWindow.Instance?.UpdateDebugVisibility();
            MainWindow.Instance?.UpdateVersionBadge();
            App.Devices.Start(s.PollIntervalSeconds);
            App.GameAuto?.UpdatePollInterval(s.GameAutoPollIntervalMs);
            App.RefreshWidget();

            SavedHintText.Text = startupOk
                ? $"已保存 ({DateTime.Now:HH:mm:ss})：轮询 {hidSeconds} 秒 · 游戏监测 {gameMs} 毫秒 · 低电阈值 {lowBat}%"
                : $"设置已保存，但开机自启写入失败：{startupDetail}";
            StartupHintText.Text = StartupStateHint(s.LaunchOnStartup, startupDetail, !startupOk);
        }
        catch (Exception ex)
        {
            SavedHintText.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_checking || _downloading) return;
        _checking = true;
        try
        {
            App.Settings.Current.GitHubMirrorUrl = GetSelectedMirrorUrl();
            App.Settings.Current.UpdateRepoSlug = NormalizeRepoSlug(App.Settings.Current.UpdateRepoSlug);
            App.Settings.Save();

            CheckUpdateBtn.IsEnabled = false;
            DownloadUpdateBtn.IsVisible = false;
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.IsIndeterminate = true;
            UpdateStatusText.Text = "正在检查最新版本...";

            var outcome = await App.Updater.CheckAsync();
            RenderUpdateOutcome(outcome);
            MainWindow.Instance?.UpdateVersionBadge();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.IsVisible = App.Updater.IsDownloading;
            CheckUpdateBtn.IsEnabled = !App.Updater.IsDownloading;
            _checking = false;
        }
    }

    private async void OnDownloadUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_downloading || _checking) return;
        var release = App.Updater.LastOutcome?.Release;
        if (release == null) return;

        _downloading = true;
        try
        {
            CheckUpdateBtn.IsEnabled = false;
            DownloadUpdateBtn.IsEnabled = false;
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 2;

            var progress = new Progress<(double Percent, string Status)>(p =>
            {
                UpdateProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                UpdateStatusText.Text = p.Status;
            });

            await App.Updater.DownloadAndApplyAsync(release, progress);
            MainWindow.Instance?.UpdateVersionBadge();
        }
        catch (Exception ex)
        {
            UpdateProgressBar.IsVisible = false;
            UpdateStatusText.Text = $"自动更新失败：{ex.Message}";
            CheckUpdateBtn.IsEnabled = true;
            DownloadUpdateBtn.IsEnabled = true;
        }
        finally
        {
            _downloading = false;
        }
    }

    private void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string slug = NormalizeRepoSlug(App.Settings.Current.UpdateRepoSlug);
            string url = App.Updater.LastOutcome?.Release?.HtmlUrl is { Length: > 0 } h
                ? h
                : $"https://github.com/{slug}/releases";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"无法打开浏览器：{ex.Message}";
        }
    }

    private void RenderUpdateOutcome(UpdateCheckOutcome? outcome)
    {
        if (App.Updater.IsDownloading) return;

        if (outcome == null)
        {
            DownloadUpdateBtn.IsVisible = false;
            ReleaseNotesCard.IsVisible = false;
            try
            {
                if (File.Exists(AppUpdateService.LastResultFilePath))
                {
                    string last = File.ReadAllText(AppUpdateService.LastResultFilePath).Trim();
                    if (last.StartsWith($"SUCCESS {AppUpdateService.CurrentVersion}", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateStatusText.Text = $"已自动更新至最新 v{AppUpdateService.CurrentVersion}。点击「立即检查更新」可随时再次向 GitHub 查询。";
                        return;
                    }
                }
            }
            catch { }

            UpdateStatusText.Text = $"当前运行 v{AppUpdateService.CurrentVersion}；仓库：{App.Settings.Current.UpdateRepoSlug}";
            return;
        }

        string channel = outcome.UsedMirror ? "加速通道" : "GitHub 官方直连";
        string warn = string.IsNullOrWhiteSpace(outcome.MirrorFallbackWarning) ? "" : $"（{outcome.MirrorFallbackWarning}）";

        if (!outcome.Success)
        {
            DownloadUpdateBtn.IsVisible = false;
            ReleaseNotesCard.IsVisible = false;
            UpdateStatusText.Text = $"{outcome.ErrorMessage}{warn}";
            return;
        }

        if (outcome.HasUpdate && outcome.Release != null)
        {
            bool usePatch = File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.ui.xaml.dll")) &&
                            !string.IsNullOrWhiteSpace(outcome.Release.RawPatchUrl);
            long sizeBytes = usePatch ? outcome.Release.PatchSize : outcome.Release.FullSize;
            string sizeLabel = sizeBytes > 0 ? $"（{(sizeBytes / 1024.0 / 1024.0):F2} MB {(usePatch ? "增量补丁" : "完整包")}）" : "";

            DownloadUpdateBtn.Content = $"立即下载 v{outcome.LatestVersion} 并重启更新";
            DownloadUpdateBtn.IsVisible = true;
            DownloadUpdateBtn.IsEnabled = true;
            UpdateStatusText.Text = $"发现新版本 v{outcome.LatestVersion}{sizeLabel}！来源：{channel}{warn}";

            ReleaseNotesTitleText.Text = $"GearDeck {outcome.Release.Tag} 更新说明";
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(outcome.Release.Notes)
                ? "无附加说明"
                : outcome.Release.Notes;
            ReleaseNotesCard.IsVisible = true;
        }
        else
        {
            DownloadUpdateBtn.IsVisible = false;
            ReleaseNotesCard.IsVisible = false;
            UpdateStatusText.Text = $"当前 v{outcome.CurrentVersion} 已是最新版本（远端最新：v{outcome.LatestVersion} · 检测通道：{channel}）{warn}";
        }
    }

    private async void OnSyncCloudImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            App.Settings.Current.CustomCloudGalleryManifestUrl = CloudManifestUrlBox.Text?.Trim() ?? "";
            App.Settings.Save();

            SyncCloudImagesBtn.IsEnabled = false;
            ImageSyncHintText.Text = "正在重载规则并从云端检查未匹配设备图片...";
            int count = await DeviceImageService.EnsureAutoCloudImagesAsync(App.LastDevices, forceRecheck: true);
            ImageSyncHintText.Text = count > 0
                ? $"同步完成：已为 {count} 台设备更新云端图片。"
                : "规则已重载；当前所有已知设备均有准确本地/缓存图样。";
        }
        catch (Exception ex)
        {
            ImageSyncHintText.Text = $"云端图片同步失败：{ex.Message}";
        }
        finally
        {
            SyncCloudImagesBtn.IsEnabled = true;
            _syncing = false;
        }
    }
}
