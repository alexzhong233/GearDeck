using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GearDeck.Core.Models;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Controls;

/// <summary>
/// 设备图片模态窗：预设图、本地上传、清除覆盖，全部通过 DeviceImageService 落盘。
/// </summary>
public partial class DeviceImagePickerDialog : Window
{
    private static readonly FontFamily IconFont = UiBuilders.IconFont;

    private readonly PeripheralDevice _device;
    private readonly HashSet<string> _recommendedIds = new(StringComparer.OrdinalIgnoreCase);
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
        StatusText.Text = $"当前识别 ID: {device.Id} · 自动匹配规则 [{autoMatchedId}]，支持本地上传或清除覆盖。";
        RefreshPreview();

        var recommended = DeviceImageService.FindRecommendedCloudPresets(device, DeviceImageService.CuratedCloudPresets);
        foreach (var item in recommended)
            _recommendedIds.Add(item.Id);

        var items = recommended
            .Concat(DeviceImageService.CuratedCloudPresets.Where(x => !_recommendedIds.Contains(x.Id)))
            .Take(24);
        foreach (var item in items)
            AddPresetButton(item);
    }

    public static async Task<bool> ShowAsync(Window owner, PeripheralDevice device)
    {
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

    private void AddPresetButton(CloudPresetImageItem item)
    {
        var content = new StackPanel { Spacing = 4, Width = 92 };
        content.Children.Add(new TextBlock
        {
            Text = UiBuilders.FallbackGlyph(_device.Kind),
            FontFamily = IconFont,
            FontSize = 30,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{item.Brand} · {item.Name}",
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32,
        });

        var button = new Button
        {
            Content = content,
            Tag = item,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 8, 8),
        };
        if (_recommendedIds.Contains(item.Id))
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
        button.Click += OnPresetClick;
        PresetPanel.Children.Add(button);
    }

    private void RefreshPreview()
        => PreviewImage.Source = DeviceImageService.LoadImageSource(_device, 72);

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
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.ico"],
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
            StatusText.Text = $"已应用本地上传图片：{saved}";
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
                StatusText.Text = $"云端拉取失败：{item.Name}，可尝试本地上传。";
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
