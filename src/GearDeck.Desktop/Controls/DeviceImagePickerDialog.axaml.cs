using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Controls;

/// <summary>
/// 设备图片模态窗：支持实时关键词搜索、真实缩略图预览（含 SVG 栅格化）、本地上传、自定义 URL 拉取与恢复智能匹配。
/// </summary>
public partial class DeviceImagePickerDialog : Window
{
    private static readonly FontFamily IconFont = UiBuilders.IconFont;

    private readonly PeripheralDevice _device;
    private readonly HashSet<string> _recommendedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CloudPresetImageItem> _allPresets;
    private CancellationTokenSource? _thumbCts;
    private bool _changed;
    private bool _busy;

    public DeviceImagePickerDialog()
        : this(new PeripheralDevice { Id = "", Name = "", Kind = DeviceKind.Mouse, Provider = "" })
    {
    }

    public DeviceImagePickerDialog(PeripheralDevice device)
    {
        InitializeComponent();
        _device = device;

        Title = $"选择「{device.Name}」的展示图片";
        TitleText.Text = Title;
        string autoMatchedId = DeviceImageService.MatchDefaultPresetId(device);
        StatusText.Text = $"识别 ID: {device.Id} · 自动规则 [{autoMatchedId}]，支持云端图库检索、SVG/位图本地上传及直链拉取。";
        RefreshPreview();

        _allPresets = BuildUnifiedPresetList();
        var recommended = DeviceImageService.FindRecommendedCloudPresets(device, _allPresets);
        foreach (var item in recommended)
            _recommendedIds.Add(item.Id);

        RenderPresets("");
        Closed += (_, _) =>
        {
            _thumbCts?.Cancel();
            _thumbCts?.Dispose();
            _thumbCts = null;
        };
    }

    public static async Task<bool> ShowAsync(Window? owner, PeripheralDevice device)
    {
        owner ??= MainWindow.Instance;
        if (owner == null || device == null)
            return false;

        var dialog = new DeviceImagePickerDialog(device);
        try
        {
            return await dialog.ShowDialog<bool>(owner);
        }
        catch
        {
            return false;
        }
    }

    private static List<CloudPresetImageItem> BuildUnifiedPresetList()
    {
        var list = new List<CloudPresetImageItem>(DeviceImageService.CuratedCloudPresets);
        var seenUrls = list.Select(x => x.ImageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var manifest = DeviceImageService.GetEffectiveManifest();
        foreach (var g in manifest.Gallery)
        {
            if (!string.IsNullOrWhiteSpace(g.ImageUrl) && seenUrls.Add(g.ImageUrl))
                list.Add(new CloudPresetImageItem(g.Id, g.Name, g.Brand, g.MatchHint, g.ImageUrl, g.IsPiperSvg));
        }

        foreach (var r in manifest.Rules)
        {
            string? url = !string.IsNullOrWhiteSpace(r.CloudUrl)
                ? r.CloudUrl
                : (!string.IsNullOrWhiteSpace(r.PiperSvg) ? $"{DeviceImageService.PiperSvgBaseUrl}{r.PiperSvg}" : null);
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                continue;

            bool isSvg = !string.IsNullOrWhiteSpace(r.PiperSvg) || url.Contains(".svg", StringComparison.OrdinalIgnoreCase);
            string brand = !string.IsNullOrWhiteSpace(r.Name) ? r.Name.Split(' ')[0] : "Custom";
            string hint = $"{r.VidPid} {r.Vid} {string.Join(" ", r.Keywords)} {r.Kind}";
            list.Add(new CloudPresetImageItem($"rule_{r.Id}", r.Name, brand, hint, url, isSvg));
        }

        return list;
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        => RenderPresets(SearchBox.Text ?? "");

    private void RenderPresets(string query)
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        PresetPanel.Children.Clear();

        IEnumerable<CloudPresetImageItem> source;
        string trimmed = query.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            source = _allPresets
                .Where(x => _recommendedIds.Contains(x.Id))
                .Concat(_allPresets.Where(x => !_recommendedIds.Contains(x.Id)));
        }
        else
        {
            string[] terms = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            source = _allPresets.Where(item => terms.All(term =>
                item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Brand.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.MatchHint.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var visible = source.Take(36).ToList();
        PresetCountText.Text = string.IsNullOrEmpty(trimmed)
            ? $"共 {_allPresets.Count} 项（已优先推荐 {_recommendedIds.Count} 项）"
            : $"匹配 {visible.Count} / {_allPresets.Count} 项";

        var pendingThumbs = new List<(CloudPresetImageItem Item, Image Img, TextBlock Fallback)>();
        foreach (var item in visible)
        {
            var slot = AddPresetButton(item);
            if (slot != null)
                pendingThumbs.Add(slot.Value);
        }

        if (pendingThumbs.Count > 0)
            _ = LoadVisibleThumbnailsAsync(pendingThumbs, ct);
    }

    private (CloudPresetImageItem Item, Image Img, TextBlock Fallback)? AddPresetButton(CloudPresetImageItem item)
    {
        var thumbHost = new Grid
        {
            Width = 52,
            Height = 52,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var fallback = new TextBlock
        {
            Text = UiBuilders.FallbackGlyph(_device.Kind),
            FontFamily = IconFont,
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var img = new Image
        {
            Width = 50,
            Height = 50,
            Stretch = Stretch.Uniform,
            IsVisible = false,
        };

        string? cachedPath = DeviceImageService.TryGetCachedCloudPath(item.ImageUrl);
        IImage? cachedSource = cachedPath != null ? DeviceImageService.CreateSourceFromFile(cachedPath, 50) : null;
        if (cachedSource != null)
        {
            img.Source = cachedSource;
            img.IsVisible = true;
            fallback.IsVisible = false;
        }

        thumbHost.Children.Add(fallback);
        thumbHost.Children.Add(img);

        var content = new StackPanel { Spacing = 6, Width = 104 };
        content.Children.Add(thumbHost);
        content.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32,
        });

        var button = new Button
        {
            Content = content,
            Tag = item,
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 0, 8, 8),
        };
        if (_recommendedIds.Contains(item.Id))
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
        button.Click += OnPresetClick;
        PresetPanel.Children.Add(button);

        return cachedSource == null ? (item, img, fallback) : null;
    }

    private static async Task LoadVisibleThumbnailsAsync(
        List<(CloudPresetImageItem Item, Image Img, TextBlock Fallback)> pending,
        CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(4, 4);
        var tasks = pending.Select(async entry =>
        {
            if (ct.IsCancellationRequested) return;
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;
                string? path = await DeviceImageService.DownloadAndCacheCloudImageAsync(entry.Item).ConfigureAwait(false);
                if (ct.IsCancellationRequested || path == null) return;
                var bmp = DeviceImageService.CreateSourceFromFile(path, 50);
                if (ct.IsCancellationRequested || bmp == null) return;

                App.UiPost(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    entry.Img.Source = bmp;
                    entry.Img.IsVisible = true;
                    entry.Fallback.IsVisible = false;
                });
            }
            catch { }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { }
    }

    private void RefreshPreview()
        => PreviewImage.Source = DeviceImageService.LoadImageSource(_device, 74);

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        var button = sender as Button;
        if (button != null)
            button.IsEnabled = false;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择设备图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.ico", "*.svg"],
                    },
                ],
            });
            var file = files.FirstOrDefault();
            string? path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "未选择文件或无法读取本地路径。";
                return;
            }

            string saved = DeviceImageService.SaveUserUploadedImage(_device.Id, path);
            RefreshPreview();
            _changed = true;
            StatusText.Text = $"已应用本地上传图片：{System.IO.Path.GetFileName(saved)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"上传失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            if (button != null)
                button.IsEnabled = true;
        }
    }

    private async void OnPullUrlClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        string url = UrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "请先输入有效的图片直链 URL。";
            return;
        }

        _busy = true;
        PullUrlBtn.IsEnabled = false;
        try
        {
            StatusText.Text = "正在拉取自定义 URL 图片...";
            bool isSvg = url.Contains(".svg", StringComparison.OrdinalIgnoreCase);
            string? cached = await DeviceImageService.DownloadAndCacheUrlAsync(url, stripPiperLayers: isSvg);
            if (cached == null)
            {
                StatusText.Text = "无法从此 URL 下载有效图片，请确认直链可访问。";
                return;
            }

            App.Settings.Current.DeviceImageOverrides[_device.Id] = cached;
            App.Settings.Save();
            RefreshPreview();
            _changed = true;
            StatusText.Text = "已缓存并应用自定义云端 URL 图片。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"拉取失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            PullUrlBtn.IsEnabled = true;
        }
    }

    private async void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button button || button.Tag is not CloudPresetImageItem item)
            return;
        _busy = true;
        button.IsEnabled = false;

        try
        {
            StatusText.Text = $"正在拉取预设：{item.Name}...";
            string? cached = await DeviceImageService.DownloadAndCacheCloudImageAsync(item);
            if (cached == null)
            {
                StatusText.Text = $"云端拉取失败：{item.Name}，可尝试本地上传或配置镜像。";
                return;
            }

            App.Settings.Current.DeviceImageOverrides[_device.Id] = cached;
            App.Settings.Save();
            RefreshPreview();
            _changed = true;
            StatusText.Text = $"已应用预设：{item.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"应用预设失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            button.IsEnabled = true;
        }
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        try
        {
            DeviceImageService.ClearOverride(_device.Id);
            RefreshPreview();
            _changed = true;
            StatusText.Text = "已清除覆盖，恢复为自动匹配图样。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除覆盖失败：{ex.Message}";
        }
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close(_changed);
}
