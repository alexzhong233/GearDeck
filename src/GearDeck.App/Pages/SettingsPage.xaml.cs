using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.App.Controls;
using GearDeck.App.Services;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _binding;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Updater.StateChanged += OnUpdateStateChanged;
        _binding = true;
        CloseToTraySwitch.IsOn = App.Settings.Current.CloseToTray;
        StartupSwitch.IsOn = App.Settings.Current.LaunchOnStartup;
        NotifySwitch.IsOn = App.Settings.Current.NotifyOnProfileSwitch;
        DebugSwitch.IsOn = App.Settings.Current.DebugModeEnabled;
        GlobalLowBatNotifySwitch.IsOn = App.Settings.Current.GlobalLowBatteryNotificationEnabled;
        IntervalBox.Value = App.Settings.Current.PollIntervalSeconds;
        CloudManifestUrlBox.Text = App.Settings.Current.CustomCloudGalleryManifestUrl;
        CurrentVersionBadgeText.Text = $"当前版本 v{AppUpdateService.CurrentVersion}";
        AutoCheckUpdateSwitch.IsOn = App.Settings.Current.CheckUpdateOnStartup;
        GitHubMirrorBox.Text = App.Settings.Current.GitHubMirrorUrl;
        _binding = false;
        RenderUpdateOutcome(App.Updater.LastOutcome);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Updater.StateChanged -= OnUpdateStateChanged;
    }

    private void OnUpdateStateChanged()
        => App.UiQueue?.TryEnqueue(() => RenderUpdateOutcome(App.Updater.LastOutcome));

    private void OnAutoCheckUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.CheckUpdateOnStartup = AutoCheckUpdateSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnMirrorPresetClear(object sender, RoutedEventArgs e)
    {
        GitHubMirrorBox.Text = "";
        SaveMirrorSetting("已清空自定义镜像站，将直连 GitHub。");
    }

    private void OnMirrorPresetGhfast(object sender, RoutedEventArgs e)
    {
        GitHubMirrorBox.Text = "https://ghfast.top/";
        SaveMirrorSetting("已设置并保存镜像站：https://ghfast.top/");
    }

    private void OnMirrorPresetGhProxy(object sender, RoutedEventArgs e)
    {
        GitHubMirrorBox.Text = "https://gh-proxy.com/";
        SaveMirrorSetting("已设置并保存镜像站：https://gh-proxy.com/");
    }

    private void OnSaveMirrorOnly(object sender, RoutedEventArgs e)
        => SaveMirrorSetting("自定义 GitHub 镜像站配置已保存。");

    private void SaveMirrorSetting(string feedback)
    {
        App.Settings.Current.GitHubMirrorUrl = GitHubMirrorBox.Text?.Trim() ?? "";
        App.Settings.Current.CheckUpdateOnStartup = AutoCheckUpdateSwitch.IsOn;
        App.Settings.Save();
        UpdateStatusText.Text = feedback;
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        SaveMirrorSetting("正在检查 GitHub 最新版本...");
        CheckUpdateBtn.IsEnabled = false;
        DownloadUpdateBtn.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        try
        {
            var outcome = await App.Updater.CheckAsync();
            RenderUpdateOutcome(outcome);
        }
        finally
        {
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Visibility = App.Updater.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
            CheckUpdateBtn.IsEnabled = !App.Updater.IsDownloading;
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        var release = App.Updater.LastOutcome?.Release;
        if (release == null) return;

        CheckUpdateBtn.IsEnabled = false;
        DownloadUpdateBtn.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 2;

        var progress = new Progress<(double Percent, string Status)>(p =>
        {
            UpdateProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            UpdateStatusText.Text = p.Status;
        });

        try
        {
            await App.Updater.DownloadAndApplyAsync(release, progress);
        }
        catch (Exception ex)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"自动更新失败：{ex.Message}";
            CheckUpdateBtn.IsEnabled = true;
            DownloadUpdateBtn.IsEnabled = true;
        }
    }

    private void OnOpenGitHubReleasesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string slug = App.Settings.Current.UpdateRepoSlug;
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
        if (App.Updater.IsDownloading)
            return;

        if (outcome == null)
        {
            DownloadUpdateBtn.Visibility = Visibility.Collapsed;
            ReleaseNotesCard.Visibility = Visibility.Collapsed;
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

        string channel = outcome.UsedMirror ? "自定义镜像站" : "GitHub 直连";
        string warn = string.IsNullOrWhiteSpace(outcome.MirrorFallbackWarning) ? "" : $"（{outcome.MirrorFallbackWarning}）";

        if (!outcome.Success)
        {
            DownloadUpdateBtn.Visibility = Visibility.Collapsed;
            ReleaseNotesCard.Visibility = Visibility.Collapsed;
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
            DownloadUpdateBtn.Visibility = Visibility.Visible;
            DownloadUpdateBtn.IsEnabled = true;
            UpdateStatusText.Text = $"发现新版本 v{outcome.LatestVersion}{sizeLabel}！来源：{channel}{warn}";

            ReleaseNotesTitleText.Text = $"GearDeck {outcome.Release.Tag} 更新说明";
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(outcome.Release.Notes)
                ? "无附加说明"
                : outcome.Release.Notes;
            ReleaseNotesCard.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadUpdateBtn.Visibility = Visibility.Collapsed;
            ReleaseNotesCard.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"当前 v{outcome.CurrentVersion} 已是最新版本（远端最新：v{outcome.LatestVersion} · 检测通道：{channel}）{warn}";
        }
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.CloseToTray = CloseToTraySwitch.IsOn;
        App.Settings.Save();
    }

    private void OnNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.NotifyOnProfileSwitch = NotifySwitch.IsOn;
        App.Settings.Save();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        bool enable = StartupSwitch.IsOn;
        var (ok, detail) = SettingsService.ApplyStartupRegistry(enable);
        if (!ok)
        {
            _binding = true;
            StartupSwitch.IsOn = !enable;
            _binding = false;
            SavedHintText.Text = $"设置开机自启失败：{detail}";
            return;
        }

        App.Settings.Current.LaunchOnStartup = enable;
        App.Settings.Save();
        SavedHintText.Text = enable
            ? $"已开启开机自启 ({DateTime.Now:HH:mm:ss})"
            : $"已关闭开机自启 ({DateTime.Now:HH:mm:ss})";
    }

    private void OnDebugToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.DebugModeEnabled = DebugSwitch.IsOn;
        App.Settings.Save();
        MainWindow.Instance?.UpdateDebugVisibility();
    }

    private void OnGlobalLowBatNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.GlobalLowBatteryNotificationEnabled = GlobalLowBatNotifySwitch.IsOn;
        App.Settings.Save();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.CloseToTray = CloseToTraySwitch.IsOn;
        App.Settings.Current.LaunchOnStartup = StartupSwitch.IsOn;
        App.Settings.Current.NotifyOnProfileSwitch = NotifySwitch.IsOn;
        App.Settings.Current.DebugModeEnabled = DebugSwitch.IsOn;
        App.Settings.Current.GlobalLowBatteryNotificationEnabled = GlobalLowBatNotifySwitch.IsOn;
        App.Settings.Current.PollIntervalSeconds = (int)IntervalBox.Value;
        App.Settings.Current.CheckUpdateOnStartup = AutoCheckUpdateSwitch.IsOn;
        App.Settings.Current.GitHubMirrorUrl = GitHubMirrorBox.Text?.Trim() ?? "";
        App.Settings.Save();
        var (startupOk, startupDetail) = SettingsService.ApplyStartupRegistry(StartupSwitch.IsOn);
        MainWindow.Instance?.UpdateDebugVisibility();
        App.Devices.Start(App.Settings.Current.PollIntervalSeconds);
        App.RefreshWidget();
        SavedHintText.Text = startupOk
            ? $"已保存 ({DateTime.Now:HH:mm:ss})"
            : $"设置已保存，但开机自启写入失败：{startupDetail}";
    }

    private void OnOpenLocalImageManifest(object sender, RoutedEventArgs e)
    {
        try
        {
            DeviceImageService.EnsureLocalManifestSeeded();
            Process.Start(new ProcessStartInfo(DeviceImageService.LocalManifestPath) { UseShellExecute = true });
            ImageSyncHintText.Text = $"已打开：{DeviceImageService.LocalManifestPath}";
        }
        catch (Exception ex)
        {
            ImageSyncHintText.Text = $"无法打开规则文件：{ex.Message}";
        }
    }

    private async void OnSyncCloudImages(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.CustomCloudGalleryManifestUrl = CloudManifestUrlBox.Text?.Trim() ?? "";
        App.Settings.Save();

        SyncCloudImagesBtn.IsEnabled = false;
        ImageSyncHintText.Text = "正在重载规则并从云端检查未匹配设备图片...";
        try
        {
            int count = await DeviceImageService.EnsureAutoCloudImagesAsync(App.LastDevices, forceRecheck: true);
            ImageSyncHintText.Text = count > 0
                ? $"同步完成：已为 {count} 台设备静默更新云端图片。"
                : "规则已重载；当前所有已知设备均有准确本地/缓存图样。";
        }
        finally
        {
            SyncCloudImagesBtn.IsEnabled = true;
        }
    }
}
