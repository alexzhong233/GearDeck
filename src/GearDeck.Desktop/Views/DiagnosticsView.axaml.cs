using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using GearDeck.Core;
using GearDeck.Core.Hid;

namespace GearDeck.Desktop.Views;

public partial class DiagnosticsView : UserControl
{
    // 只展示非敏感的诊断用环境变量，避免把令牌/密钥写进日志面板。
    private static readonly string[] SnapshotKeys =
    [
        "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "DOTNET_ROOT", "DOTNET_ENVIRONMENT",
        "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "GEARDECK_LOG_LEVEL",
    ];

    private bool _copying;

    public DiagnosticsView()
    {
        InitializeComponent();

        PlatformInfo.Text =
            $"运行平台：{RuntimeInformation.OSDescription}（{RuntimeInformation.ProcessArchitecture}）" + Environment.NewLine +
            $"系统版本：{Environment.OSVersion}" + Environment.NewLine +
            $".NET 版本：{RuntimeInformation.FrameworkDescription}";

        MacPermissionNotice.IsVisible = OperatingSystem.IsMacOS();
        RefreshLog();
    }

    private void OnRefreshLogClick(object? sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        var lines = Diag.Snapshot();
        LogText.Text = lines.Count == 0
            ? "（暂无日志）"
            : string.Join(Environment.NewLine, lines);
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 环境变量快照 ==");
            foreach (string key in SnapshotKeys)
            {
                string? value = Environment.GetEnvironmentVariable(key);
                sb.AppendLine(string.IsNullOrWhiteSpace(value) ? $"{key} = （未设置）" : $"{key} = {value}");
            }

            var all = Environment.GetEnvironmentVariables();
            sb.AppendLine($"环境变量总数：{all.Count}");
            string? path = Environment.GetEnvironmentVariable("PATH");
            sb.AppendLine($"PATH 条目数：{path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length ?? 0}");
            sb.AppendLine();

            int loaded = LoadedProviderCount();
            int recognized = App.LastDevices.Select(d => d.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            sb.AppendLine("== 已加载驱动快照 ==");
            sb.AppendLine($"已加载 Provider 驱动数量：{loaded}");
            sb.AppendLine($"已识别设备驱动种类：{recognized}");
            sb.AppendLine($"已发现设备数量：{App.LastDevices.Count}");
            sb.AppendLine();

            var unknown = UnknownHidScanner.Scan(App.LastDevices);
            sb.AppendLine($"== 未识别外设 HID 指纹 ({unknown.Count}) ==");
            sb.AppendLine(UnknownHidScanner.FormatSummary(unknown));

            SnapshotText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            Diag.Log($"生成诊断快照失败: {ex.Message}");
            SnapshotText.Text = $"无法生成快照：{ex.Message}";
        }
    }

    private static int LoadedProviderCount()
    {
        try
        {
            // DeviceManager 未公开 Provider 列表，只读反射其内部集合做快照。
            var field = typeof(DeviceManager).GetField("_providers", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(App.Devices) is ICollection providers)
                return providers.Count;
        }
        catch { }

        return App.LastDevices.Select(d => d.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        if (_copying) return;
        _copying = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(LogText.Text ?? string.Empty);
                ActionHintText.Text = "诊断日志已复制到剪贴板。";
            }
            else
            {
                ActionHintText.Text = "无法访问剪贴板。";
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"复制诊断日志失败: {ex.Message}");
            ActionHintText.Text = $"复制失败：{ex.Message}";
        }
        finally
        {
            _copying = false;
        }
    }

    private async void OnCopyUnknownHidClick(object? sender, RoutedEventArgs e)
    {
        if (_copying) return;
        _copying = true;
        try
        {
            var items = UnknownHidScanner.Scan(App.LastDevices);
            string json = UnknownHidScanner.SerializeJson(items);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(json);
                string msg = items.Count == 0
                    ? "当前无未识别外设，已复制空列表 JSON。"
                    : $"已复制 {items.Count} 台未识别外设 HID 指纹 JSON。";
                ActionHintText.Text = msg;
                MainWindow.ShowToast(msg, InfoBarSeverity.Success);
            }
            else
            {
                ActionHintText.Text = "无法访问剪贴板。";
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"复制未知外设指纹失败: {ex.Message}");
            ActionHintText.Text = $"复制失败：{ex.Message}";
        }
        finally
        {
            _copying = false;
        }
    }

    private void OnOpenLogDirClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = ResolveLogsDirectory();
            Directory.CreateDirectory(dir);

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add(dir);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            else
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(dir);
                Process.Start(psi);
            }

            ActionHintText.Text = $"已打开日志目录：{dir}";
            Diag.Log($"已打开日志目录: {dir}");
        }
        catch (Exception ex)
        {
            Diag.Log($"打开日志目录失败: {ex.Message}");
            ActionHintText.Text = $"打开日志目录失败：{ex.Message}";
        }
    }

    private static string ResolveLogsDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string primary = Path.Combine(OperatingSystem.IsWindows() ? local : roaming, "GearDeck", "logs");
        string alternate = Path.Combine(local, "GearDeck", "logs");
        return !Directory.Exists(primary) && Directory.Exists(alternate) ? alternate : primary;
    }
}
