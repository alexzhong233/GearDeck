using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GearDeck.Core;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

internal enum Win32RegistryHive
{
    LocalMachine,
    CurrentUser,
}

internal enum Win32RegistryView
{
    Registry64,
    Registry32,
}

/// <summary>基于 advapi32 P/Invoke 的只读注册表访问器，避免依赖 Microsoft.Win32.Registry / Microsoft.Win32 命名空间。</summary>
internal static class Wow64RegistryReader
{
    private const uint KeyRead = 0x00020019;
    private const uint KeyWow64_64Key = 0x0100;
    private const uint KeyWow64_32Key = 0x0200;
    private const int ErrorMoreData = 234;

    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));

    internal static RegistryKeyHandle? OpenBaseKey(Win32RegistryHive hive, Win32RegistryView view)
    {
        IntPtr root = hive == Win32RegistryHive.LocalMachine ? HkeyLocalMachine : HkeyCurrentUser;
        uint flags = view == Win32RegistryView.Registry32 ? KeyWow64_32Key : KeyWow64_64Key;
        return Open(root, string.Empty, flags);
    }

    private static RegistryKeyHandle? Open(IntPtr parent, string subKey, uint extraFlags)
    {
        int rc = RegOpenKeyExW(parent, subKey, 0, KeyRead | extraFlags, out IntPtr result);
        return rc == 0 && result != IntPtr.Zero ? new RegistryKeyHandle(result) : null;
    }

    public sealed class RegistryKeyHandle : IDisposable
    {
        private IntPtr _handle;

        internal RegistryKeyHandle(IntPtr handle) => _handle = handle;

        public RegistryKeyHandle? OpenSubKey(string path) => Open(_handle, path, 0);

        public IReadOnlyList<string> GetSubKeyNames()
        {
            var names = new List<string>();
            for (uint i = 0; ; i++)
            {
                var sb = new StringBuilder(256);
                uint len = (uint)sb.Capacity;
                int rc = RegEnumKeyExW(_handle, i, sb, ref len,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0) break;
                names.Add(sb.ToString());
            }
            return names;
        }

        public string? GetStringValue(string name)
        {
            uint size = 512;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var data = new byte[size];
                uint len = size;
                int rc = RegQueryValueExW(_handle, name, IntPtr.Zero, out uint type, data, ref len);
                if (rc == ErrorMoreData)
                {
                    size *= 2;
                    continue;
                }
                if (rc != 0 || (type != 1 && type != 2)) return null;
                return Encoding.Unicode.GetString(data, 0, (int)Math.Min(len, data.Length)).TrimEnd('\0');
            }
            return null;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            RegCloseKey(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegEnumKeyExW(IntPtr hKey, uint dwIndex, StringBuilder lpName, ref uint lpcchName,
        IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType,
        byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}

public static class VendorDriverLauncher
{
    public static (bool Launched, string Message) LaunchLocal(LocalDriverInfo info)
    {
        ExternalConfiguratorGuard.PauseForExternalDriver(info.DisplayName, TimeSpan.FromMinutes(3));

        if (OperatingSystem.IsMacOS())
            return LaunchMacLocal(info);

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

    private static (bool Launched, string Message) LaunchMacLocal(LocalDriverInfo info)
    {
        string? app = FindMacApplication(info);
        if (!string.IsNullOrWhiteSpace(app) && TryShellStart("open", $"-a \"{app}\""))
            return (true, $"已启动 {info.DisplayName}（macOS 应用），后台轮询已自动暂停 180 秒以避免抢占控制权。");

        if (TryOpenWebBrowser(info.DownloadUrl, preferChromium: false))
            return (false, $"未在 /Applications 检测到已安装的 {info.DisplayName}，已打开官方下载页（并暂停后台轮询）。");

        return (false, $"本机未检测到 {info.DisplayName}，也无法打开官方下载页，请手动下载安装。");
    }

    private static string? FindMacApplication(LocalDriverInfo info)
    {
        var roots = new List<string> { "/Applications" };
        string userApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
        if (Directory.Exists(userApps))
            roots.Add(userApps);

        var hints = info.UninstallNameHints
            .Concat(info.ProcessNames)
            .Append(info.DisplayName)
            .Select(NormalizeName)
            .Where(s => s.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string app in Directory.EnumerateDirectories(root, "*.app", SearchOption.TopDirectoryOnly))
            {
                string name = NormalizeName(Path.GetFileNameWithoutExtension(app));
                if (hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                                   h.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    return app;
            }
        }
        return null;
    }

    private static string NormalizeName(string value)
        => string.Concat(value.Where(c => char.IsLetterOrDigit(c))).ToLowerInvariant();

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

            using var baseKey = Wow64RegistryReader.OpenBaseKey(
                Win32RegistryHive.CurrentUser, Win32RegistryView.Registry64);
            using var famKey = baseKey?.OpenSubKey(
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
        Win32RegistryHive[] hives = [Win32RegistryHive.LocalMachine, Win32RegistryHive.CurrentUser];
        Win32RegistryView[] views = [Win32RegistryView.Registry64, Win32RegistryView.Registry32];
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = Wow64RegistryReader.OpenBaseKey(hive, view);
                    using var key = baseKey?.OpenSubKey(uninstallPath);
                    if (key == null) continue;

                    foreach (string subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        string? disp = sub?.GetStringValue("DisplayName");
                        if (string.IsNullOrWhiteSpace(disp) || !disp.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (disp.Contains("Service", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? icon = sub?.GetStringValue("DisplayIcon")?.Split(',')[0].Trim('"', ' ');
                        if (!string.IsNullOrWhiteSpace(icon) &&
                            icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            !IsBackgroundOrInstallerExe(icon) &&
                            File.Exists(icon))
                            return icon;

                        string? installLoc = sub?.GetStringValue("InstallLocation")?.Trim('"', ' ');
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
        if (OperatingSystem.IsMacOS())
        {
            if (preferChromium)
            {
                foreach (string app in new[]
                         {
                             "/Applications/Google Chrome.app",
                             "/Applications/Microsoft Edge.app",
                             "/Applications/Chromium.app",
                         })
                {
                    if (Directory.Exists(app) && TryShellStart("open", $"-a \"{app}\" \"{url}\""))
                        return true;
                }
            }
            return TryShellStart("open", $"\"{url}\"");
        }

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
