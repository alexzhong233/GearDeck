using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using GearDeck.Core;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Services;

/// <summary>应用版本检查、自定义 GitHub 镜像下载、SHA256 校验与自更新重启服务（Avalonia Desktop 版）。</summary>
public sealed class AppUpdateService
{
    private static readonly string UpdatesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GearDeck",
        "updates");

    public static string LastResultFilePath => Path.Combine(UpdatesDir, "last-update-result.txt");

    public static string CurrentVersion
    {
        get
        {
            var infoVer = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
                return UpdateClient.NormalizeVersionString(infoVer);

            var asmVer = typeof(App).Assembly.GetName().Version;
            if (asmVer != null)
                return $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}";

            return "1.0.0";
        }
    }

    public UpdateCheckOutcome? LastOutcome { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }

    public event Action? StateChanged;

    public async Task<UpdateCheckOutcome> CheckAsync(bool allowDirectFallback = true, CancellationToken ct = default)
    {
        if (IsChecking && LastOutcome != null)
            return LastOutcome;

        IsChecking = true;
        NotifyStateChanged();
        try
        {
            string slug = App.Settings.Current.UpdateRepoSlug;
            string mirror = App.Settings.Current.GitHubMirrorUrl;
            var outcome = await UpdateClient.CheckForUpdateAsync(
                slug,
                CurrentVersion,
                mirror,
                allowDirectFallback,
                ct: ct);

            LastOutcome = outcome;
            return outcome;
        }
        finally
        {
            IsChecking = false;
            NotifyStateChanged();
        }
    }

    public async Task DownloadAndApplyAsync(
        ReleaseUpdateInfo release,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsDownloading)
            throw new InvalidOperationException("更新下载任务正在进行中。");

        IsDownloading = true;
        NotifyStateChanged();

        try
        {
            Directory.CreateDirectory(UpdatesDir);
            if (OperatingSystem.IsMacOS())
            {
                await DownloadAndRevealOnMacAsync(release, progress, ct);
                return;
            }

            bool hasRuntime = File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.ui.xaml.dll"));
            bool usePatch = hasRuntime && !string.IsNullOrWhiteSpace(release.RawPatchUrl);

            string preferredUrl = usePatch ? release.PatchUrl : release.DownloadUrl;
            string fallbackUrl = usePatch ? release.RawPatchUrl : release.RawDownloadUrl;
            string expectedSha = usePatch ? release.PatchSha256 : release.FullSha256;

            if (string.IsNullOrWhiteSpace(preferredUrl))
            {
                usePatch = false;
                preferredUrl = release.DownloadUrl;
                fallbackUrl = release.RawDownloadUrl;
                expectedSha = release.FullSha256;
            }

            if (string.IsNullOrWhiteSpace(preferredUrl))
                throw new InvalidOperationException("发布清单中未包含可用的更新包下载地址。");

            string zipPath = Path.Combine(UpdatesDir, $"GearDeck-{release.Tag}-{(usePatch ? "patch" : "full")}.zip");
            string stagingDir = Path.Combine(UpdatesDir, "staging");

            await DownloadAndVerifyZipWithFallbackAsync(
                preferredUrl,
                fallbackUrl,
                zipPath,
                expectedSha,
                progress,
                ct);

            progress?.Report((94, "正在解压更新文件到暂存目录..."));
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            string stagedExe = Path.Combine(stagingDir, "GearDeck.Desktop.exe");
            if (!File.Exists(stagedExe))
                throw new InvalidDataException("更新包中缺少 GearDeck.Desktop.exe，已中止替换以保护现有安装。");

            string targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetExe = Path.Combine(targetDir, "GearDeck.Desktop.exe");
            string scriptPath = Path.Combine(UpdatesDir, "apply-update.ps1");

            progress?.Report((98, "正在准备自动替换并重启 GearDeck Desktop..."));
            WriteApplyScript(scriptPath);

            int pid = Environment.ProcessId;
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-WaitPid");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("-StagingDir");
            psi.ArgumentList.Add(stagingDir);
            psi.ArgumentList.Add("-TargetDir");
            psi.ArgumentList.Add(targetDir);
            psi.ArgumentList.Add("-TargetExe");
            psi.ArgumentList.Add(targetExe);
            psi.ArgumentList.Add("-ResultFile");
            psi.ArgumentList.Add(LastResultFilePath);
            psi.ArgumentList.Add("-Version");
            psi.ArgumentList.Add(release.Version);

            Process.Start(psi);
            progress?.Report((100, $"即将退出并升级到 v{release.Version}..."));

            await Task.Delay(250, ct);
            App.Exit();
        }
        finally
        {
            IsDownloading = false;
            NotifyStateChanged();
        }
    }

    /// <summary>macOS 不执行 PowerShell 自替换：下载并校验安装包后在访达中显示；无可用包时直接打开发布页。</summary>
    private static async Task DownloadAndRevealOnMacAsync(
        ReleaseUpdateInfo release,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken ct)
    {
        string preferredUrl = !string.IsNullOrWhiteSpace(release.DownloadUrl)
            ? release.DownloadUrl
            : release.InstallerUrl;
        string fallbackUrl = !string.IsNullOrWhiteSpace(release.RawDownloadUrl)
            ? release.RawDownloadUrl
            : release.RawInstallerUrl;
        string expectedSha = !string.IsNullOrWhiteSpace(release.FullSha256)
            ? release.FullSha256
            : release.InstallerSha256;

        if (string.IsNullOrWhiteSpace(preferredUrl))
        {
            progress?.Report((100, "当前版本没有可用的安装包，正在打开发布页面..."));
            if (!string.IsNullOrWhiteSpace(release.HtmlUrl))
                TryMacOpen(release.HtmlUrl);
            return;
        }

        string assetPath = Path.Combine(UpdatesDir, $"GearDeck-{release.Tag}-mac");
        await DownloadAndVerifyAssetWithFallbackAsync(
            preferredUrl, fallbackUrl, assetPath, expectedSha, progress, ct);

        TryMacOpen("-R", assetPath);
        progress?.Report((100, "更新包已下载，请在访达中完成安装..."));
    }

    private static async Task DownloadAndVerifyAssetWithFallbackAsync(
        string preferredUrl,
        string fallbackUrl,
        string assetPath,
        string expectedSha256,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken ct)
    {
        var candidates = new List<string> { preferredUrl };
        if (!string.IsNullOrWhiteSpace(fallbackUrl) &&
            !string.Equals(preferredUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(fallbackUrl);
        }

        Exception? lastEx = null;
        for (int idx = 0; idx < candidates.Count; idx++)
        {
            string url = candidates[idx];
            bool usedCustomMirror = idx == 0 &&
                                    !string.IsNullOrWhiteSpace(fallbackUrl) &&
                                    !string.Equals(preferredUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase);
            try
            {
                progress?.Report((2, idx > 0
                    ? "镜像下载异常，已回退直连 GitHub 下载更新包..."
                    : (usedCustomMirror ? "正在通过自定义镜像下载更新包..." : "正在下载更新包...")));
                await DownloadFileAsync(url, assetPath, progress, ct);
                ValidateSha256(assetPath, expectedSha256, requireSha256: usedCustomMirror);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Diag.Log($"macOS 更新包下载校验失败 ({url}): {ex.Message}");
            }
        }

        throw new InvalidOperationException($"更新包下载或校验失败：{lastEx?.Message ?? "未知错误"}", lastEx);
    }

    private static async Task DownloadAndVerifyZipWithFallbackAsync(
        string preferredUrl,
        string fallbackUrl,
        string zipPath,
        string expectedSha256,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken ct)
    {
        var candidates = new List<string> { preferredUrl };
        if (!string.IsNullOrWhiteSpace(fallbackUrl) &&
            !string.Equals(preferredUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(fallbackUrl);
        }

        Exception? lastEx = null;
        for (int idx = 0; idx < candidates.Count; idx++)
        {
            string url = candidates[idx];
            bool isFallback = idx > 0;
            try
            {
                progress?.Report((2, isFallback ? "镜像下载异常，已回退直连 GitHub 下载更新包..." : $"正在下载更新包：{(preferredUrl == fallbackUrl ? "GitHub 直连" : "自定义镜像站")}..."));
                await DownloadFileAsync(url, zipPath, progress, ct);
                bool usedCustomMirror = !string.IsNullOrWhiteSpace(fallbackUrl) &&
                                        !string.Equals(url, fallbackUrl, StringComparison.OrdinalIgnoreCase);
                ValidateDownloadedZip(zipPath, expectedSha256, requireSha256: usedCustomMirror);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Diag.Log($"更新包下载校验失败 ({url}): {ex.Message}");
            }
        }

        throw new InvalidOperationException($"更新包下载或校验失败：{lastEx?.Message ?? "未知错误"}", lastEx);
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken ct)
    {
        using var http = UpdateClient.CreateDefaultHttpClient(timeoutSeconds: 180);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? -1;
        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);

        string tmpPath = destinationPath + ".part";
        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            byte[] buffer = new byte[65536];
            long received = 0;
            int read;
            while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (totalBytes > 0)
                {
                    double pct = Math.Clamp(received * 90.0 / totalBytes, 2, 90);
                    progress?.Report((pct, $"正在下载更新包... {received / 1024.0:F0} KB / {totalBytes / 1024.0:F0} KB ({pct:F0}%)"));
                }
                else
                {
                    progress?.Report((45, $"正在下载更新包... {received / 1024.0:F0} KB"));
                }
            }
        }

        File.Move(tmpPath, destinationPath, overwrite: true);
    }

    private static void ValidateDownloadedZip(string zipPath, string expectedSha256, bool requireSha256 = false)
    {
        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 4)
            throw new InvalidDataException("下载的更新包为空或已损坏。");

        using (var fs = File.OpenRead(zipPath))
        {
            byte[] header = new byte[2];
            int n = fs.Read(header, 0, 2);
            if (n < 2 || header[0] != (byte)'P' || header[1] != (byte)'K')
                throw new InvalidDataException("下载结果不是有效的 ZIP 压缩包（镜像站可能返回了异常页面）。");
        }

        ValidateSha256(zipPath, expectedSha256, requireSha256);

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("ZIP 更新包内没有任何文件。");
    }

    private static void ValidateSha256(string path, string expectedSha256, bool requireSha256)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 64)
            throw new InvalidDataException("下载的文件为空或已损坏。");

        if (requireSha256 && string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidDataException("自定义镜像下载缺少 SHA-256 校验值，已拒绝并回退直连 GitHub。");

        if (string.IsNullOrWhiteSpace(expectedSha256))
            return;

        using var fs = File.OpenRead(path);
        string expectedNormalized = expectedSha256.Trim().ToUpperInvariant();
        string actualSha = Convert.ToHexString(SHA256.HashData(fs)).ToUpperInvariant();
        if (!string.Equals(actualSha, expectedNormalized, StringComparison.Ordinal))
        {
            string expShort = expectedNormalized[..Math.Min(8, expectedNormalized.Length)];
            throw new InvalidDataException($"SHA-256 校验不匹配（期望 {expShort}...，实际 {actualSha[..8]}...）。");
        }
    }

    private static bool TryMacOpen(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = true };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"macOS 打开失败 ({string.Join(' ', args)}): {ex.Message}");
            return false;
        }
    }

    private static void WriteApplyScript(string scriptPath)
    {
        const string script = """
param(
    [int]$WaitPid,
    [string]$StagingDir,
    [string]$TargetDir,
    [string]$TargetExe,
    [string]$ResultFile,
    [string]$Version
)

$ErrorActionPreference = "Stop"

try {
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
        if (-not $p) { break }
        Start-Sleep -Milliseconds 200
    }

    $normTarget = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\') + '\'
    foreach ($procName in @("GearDeck.Desktop", "GearDeck.Widget", "GearDeck.App")) {
        Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $path = $_.MainModule.FileName
                if ($path -and $path.StartsWith($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }
    Start-Sleep -Milliseconds 300

    $files = [System.IO.Directory]::GetFiles($StagingDir, "*", [System.IO.SearchOption]::AllDirectories)
    foreach ($srcFile in $files) {
        $rel = $srcFile.Substring($StagingDir.Length).TrimStart('\', '/')
        $dstFile = Join-Path $TargetDir $rel
        $dstDir = Split-Path -Parent $dstFile
        if (-not (Test-Path $dstDir)) {
            [System.IO.Directory]::CreateDirectory($dstDir) | Out-Null
        }

        $copied = $false
        for ($try = 0; $try -lt 15; $try++) {
            try {
                [System.IO.File]::Copy($srcFile, $dstFile, $true)
                $copied = $true
                break
            } catch {
                Start-Sleep -Milliseconds 350
            }
        }
        if (-not $copied) {
            throw "Failed to overwrite locked file: $dstFile"
        }
    }

    try { Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }

    $stamp = (Get-Date).ToString("o")
    [System.IO.File]::WriteAllText($ResultFile, "SUCCESS $Version $stamp", (New-Object System.Text.UTF8Encoding($false)))

    Start-Process -FilePath $TargetExe -WorkingDirectory $TargetDir
}
catch {
    try {
        [System.IO.File]::WriteAllText($ResultFile, "ERROR $($_.Exception.Message)", (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
    exit 1
}
""";
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private void NotifyStateChanged()
    {
        Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
    }
}
