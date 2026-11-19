using System.Diagnostics;
using GearDeck.Core;
using GearDeck.Core.Services;
using Microsoft.Win32;

namespace GearDeck.App.Services;

public static class VendorDriverLauncher
{
    public static (bool Launched, string Message) LaunchLocal(LocalDriverInfo info)
    {
        ExternalConfiguratorGuard.PauseForExternalDriver(info.DisplayName, TimeSpan.FromMinutes(3));

        // 1. 若已在运行，尝试优先切到已装好的可执行文件
        string? exe = ResolveInstalledExe(info);
        if (!string.IsNullOrWhiteSpace(exe) && TryShellStart(exe))
            return (true, $"已启动 {info.DisplayName}，后台轮询已自动暂停 180 秒以避免抢占控制权。");

        // 2. 尝试 Store / Shell URI（如 Armoury Crate）
        if (!string.IsNullOrWhiteSpace(info.ShellUri))
        {
            if (TryShellStart("explorer.exe", info.ShellUri))
                return (true, $"已触发 {info.DisplayName}，后台轮询已自动暂停 180 秒以避免抢占控制权。");
        }

        // 3. 本地未检测到安装包时打开官方下载页面
        TryOpenWebBrowser(info.DownloadUrl, preferChromium: false);
        return (false, $"未在本机检测到已安装的 {info.DisplayName}，已为你打开官方下载页（并暂停后台轮询）。");
    }

    public static (bool Launched, string Message) LaunchWeb(WebDriverInfo info)
    {
        ExternalConfiguratorGuard.PauseForExternalDriver(info.DisplayName, TimeSpan.FromMinutes(3));
        bool ok = TryOpenWebBrowser(info.Url, preferChromium: true);
        return ok
            ? (true, $"已用支持 WebHID 的浏览器打开「{info.DisplayName}」，并暂停 GearDeck 后台轮询 180 秒以免抢占设备。")
            : (false, $"无法启动浏览器打开 {info.Url}，请手动复制在 Chrome / Edge 中打开。");
    }

    private static string? ResolveInstalledExe(LocalDriverInfo info)
    {
        foreach (string p in info.ExeCandidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string hint in info.UninstallNameHints)
        {
            string? found = SearchUninstallRegistry(hint);
            if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                return found;

            string localCandidate = Path.Combine(localApp, "Programs", hint);
            if (Directory.Exists(localCandidate))
            {
                string? exe = Directory.EnumerateFiles(localCandidate, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f => !f.Contains("unins", StringComparison.OrdinalIgnoreCase));
                if (exe != null) return exe;
            }
        }

        try
        {
            foreach (string procName in info.ProcessNames)
            {
                var running = Process.GetProcessesByName(procName).FirstOrDefault();
                string? modulePath = running?.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(modulePath) && File.Exists(modulePath))
                    return modulePath;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? SearchUninstallRegistry(string nameHint)
    {
        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        RegistryView[] views = [RegistryView.Registry64, RegistryView.Registry32];
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(uninstallPath);
                    if (key == null) continue;

                    foreach (string subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        string? disp = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(disp) || !disp.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? icon = (sub?.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"', ' ');
                        if (!string.IsNullOrWhiteSpace(icon) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon))
                            return icon;

                        string? installLoc = (sub?.GetValue("InstallLocation") as string)?.Trim('"', ' ');
                        if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                        {
                            string? exe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly)
                                .FirstOrDefault(f => !f.Contains("unins", StringComparison.OrdinalIgnoreCase) && !f.Contains("update", StringComparison.OrdinalIgnoreCase));
                            if (exe != null) return exe;
                        }
                    }
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static bool TryOpenWebBrowser(string url, bool preferChromium)
    {
        if (preferChromium)
        {
            string[] chromiumCandidates =
            [
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            ];
            foreach (string exe in chromiumCandidates)
            {
                if (File.Exists(exe) && TryShellStart(exe, $"\"{url}\""))
                    return true;
            }
        }

        return TryShellStart(url);
    }

    private static bool TryShellStart(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = args ?? string.Empty,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"启动外部驱动失败 ({target}): {ex.Message}");
            return false;
        }
    }
}
