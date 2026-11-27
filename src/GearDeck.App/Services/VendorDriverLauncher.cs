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

        // 1. 尝试明确配置或注册表里的桌面客户端 exe（排除后台 service/helper/unins）
        string? exe = ResolveInstalledExe(info);
        if (!string.IsNullOrWhiteSpace(exe) && TryShellStart(exe))
            return (true, $"已启动 {info.DisplayName}，后台轮询已自动暂停 180 秒以避免抢占控制权。");

        // 2. 尝试 UWP / Store Shell URI（如 ROG Armoury Crate）
        if (!string.IsNullOrWhiteSpace(info.ShellUri) && IsShellAppInstalled(info.ShellUri))
        {
            if (TryShellStart("explorer.exe", info.ShellUri))
                return (true, $"已启动 {info.DisplayName}，后台轮询已自动暂停 180 秒以避免抢占控制权。");
        }

        // 3. 本地未检测到安装包时打开官方下载页面
        TryOpenWebBrowser(info.DownloadUrl, preferChromium: false);
        return (false, $"未在本机检测到已安装的 {info.DisplayName}，已为你打开官方下载页（并暂停后台轮询）。");
    }

    private static bool IsBackgroundOrInstallerExe(string path)
    {
        string name = Path.GetFileName(path);
        return name.Contains("service", StringComparison.OrdinalIgnoreCase)
               || name.Contains("helper", StringComparison.OrdinalIgnoreCase)
               || name.Contains("unins", StringComparison.OrdinalIgnoreCase)
               || name.Contains("update", StringComparison.OrdinalIgnoreCase)
               || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
               || name.Contains("install", StringComparison.OrdinalIgnoreCase)
               || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
               || name.Contains("daemon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellAppInstalled(string shellUri)
    {
        const string prefix = @"shell:AppsFolder\";
        if (!shellUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        string appId = shellUri[prefix.Length..];
        int bang = appId.IndexOf('!');
        string pfn = bang > 0 ? appId[..bang] : appId;
        if (string.IsNullOrWhiteSpace(pfn))
            return true;

        try
        {
            string pkgDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                pfn);
            if (Directory.Exists(pkgDir))
                return true;

            using var famKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Families\{pfn}");
            if (famKey != null)
                return true;
        }
        catch
        {
            // 若不确定（如受限权限读取注册表失败），仍允许 explorer 触发 Shell URI
            return true;
        }

        return false;
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
                    .FirstOrDefault(f => !IsBackgroundOrInstallerExe(f));
                if (exe != null) return exe;
            }
        }

        try
        {
            foreach (string procName in info.ProcessNames)
            {
                var running = Process.GetProcessesByName(procName).FirstOrDefault();
                string? modulePath = running?.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(modulePath) && File.Exists(modulePath) && !IsBackgroundOrInstallerExe(modulePath))
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
                        if (disp.Contains("Service", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? icon = (sub?.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"', ' ');
                        if (!string.IsNullOrWhiteSpace(icon) &&
                            icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            !IsBackgroundOrInstallerExe(icon) &&
                            File.Exists(icon))
                            return icon;

                        string? installLoc = (sub?.GetValue("InstallLocation") as string)?.Trim('"', ' ');
                        if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                        {
                            string? exe = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly)
                                .FirstOrDefault(f => !IsBackgroundOrInstallerExe(f));
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
            var chromiumCandidates = new List<string>();
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                chromiumCandidates.Add(Path.Combine(folder, @"Google\Chrome\Application\chrome.exe"));
                chromiumCandidates.Add(Path.Combine(folder, @"Microsoft\Edge\Application\msedge.exe"));
            }
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
