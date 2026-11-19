using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GearDeck.App.Services;
using GearDeck.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GearDeck.App.Controls;

/// <summary>
/// OpenMouse 风格设备图片选择弹窗：
/// - 优先按 VID:PID 与型号展示智能匹配推荐图样；
/// - 支持本地图片上传、19+ 套内置高精产品矢量剪影与免费云端图库（jsDelivr + GitHub）一键拉取缓存。
/// </summary>
public static class DeviceImagePickerDialog
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, PeripheralDevice device)
    {
        bool changed = false;
        var root = new StackPanel { Spacing = 14, Width = 600 };

        var previewImg = new Image
        {
            Source = DeviceImageService.LoadImageSource(device, 72),
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
        };
        var previewBorder = new Border
        {
            Width = 82,
            Height = 82,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
            Child = previewImg,
        };

        string autoMatchedId = DeviceImageService.MatchDefaultPresetId(device);
        var statusTb = new TextBlock
        {
            Text = $"当前识别 ID: {device.Id} · 默认自动匹配剪影 [{autoMatchedId}]，支持本地上传或免费云端替换。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        ContentDialog? dialog = null;

        var uploadBtn = new Button { Content = "从本地上传图片..." };
        uploadBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".ico", ".svg" })
                    picker.FileTypeFilter.Add(ext);
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                if (MainWindow.Instance != null)
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(MainWindow.Instance));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                string saved = DeviceImageService.SaveUserUploadedImage(device.Id, file.Path);
                previewImg.Source = DeviceImageService.CreateSourceFromFile(saved, 72);
                changed = true;
                statusTb.Text = $"已应用本地上传图片：{file.Name}";
            }
            catch (Exception ex)
            {
                statusTb.Text = $"上传失败：{ex.Message}";
            }
        };

        var resetBtn = new Button { Content = "恢复 VID:PID 智能匹配" };
        resetBtn.Click += (_, _) =>
        {
            DeviceImageService.ClearOverride(device.Id);
            previewImg.Source = DeviceImageService.LoadImageSource(device, 72);
            changed = true;
            statusTb.Text = $"已恢复为智能匹配图样 [{autoMatchedId}]。";
        };

        var topBar = new Grid { ColumnSpacing = 12 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.Children.Add(previewBorder);

        var topRight = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var btnRow = new ResponsiveWrapPanel { Spacing = 8, LineSpacing = 6 };
        btnRow.Children.Add(uploadBtn);
        btnRow.Children.Add(resetBtn);
        topRight.Children.Add(btnRow);
        topRight.Children.Add(statusTb);
        Grid.SetColumn(topRight, 1);
        topBar.Children.Add(topRight);
        root.Children.Add(topBar);

        // 2. 内置产品级精准矢量造型库
        root.Children.Add(new TextBlock { Text = "内置高清硬件造型预设（按具体外设定制）", FontWeight = FontWeights.SemiBold });
        var presetGrid = new ResponsiveWrapPanel { Spacing = 8, LineSpacing = 8 };
        foreach (var preset in DeviceImageService.EnsureBuiltInPresets())
        {
            var stack = new StackPanel { Spacing = 4, Width = 92 };
            stack.Children.Add(new Image
            {
                Source = DeviceImageService.CreateSourceFromFile(preset.LocalSvgPath, 44),
                Width = 44,
                Height = 44,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = preset.Name.Split('(')[0].Trim(),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var btn = new Button { Content = stack, Padding = new Thickness(6) };
            if (string.Equals(preset.Id, autoMatchedId, StringComparison.OrdinalIgnoreCase))
                btn.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            ToolTipService.SetToolTip(btn, preset.Name);
            btn.Click += (_, _) =>
            {
                DeviceImageService.SetPresetOverride(device.Id, preset.Id);
                previewImg.Source = DeviceImageService.CreateSourceFromFile(preset.LocalSvgPath, 72);
                changed = true;
                statusTb.Text = $"已切换为造型预设：{preset.Name}";
            };
            presetGrid.Children.Add(btn);
        }
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 165,
            Content = presetGrid,
        });

        // 3. 免费云端预设图库（自动按 VID:PID + 品牌推荐置顶）
        var cloudHeader = new Grid { ColumnSpacing = 8 };
        cloudHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cloudHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cloudHeader.Children.Add(new TextBlock
        {
            Text = "免费云端图库（jsDelivr CDN · 自动按 VID:PID 推荐匹配）",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var searchBox = new TextBox { PlaceholderText = "筛选型号 (如 viper / apex / gpx)", Width = 210 };
        Grid.SetColumn(searchBox, 1);
        cloudHeader.Children.Add(searchBox);
        root.Children.Add(cloudHeader);

        var cloudListPanel = new StackPanel { Spacing = 4 };
        var cloudScroll = new ScrollViewer { MaxHeight = 145, Content = cloudListPanel };
        root.Children.Add(cloudScroll);

        List<CloudPresetImageItem> allCloudItems = [.. DeviceImageService.CuratedCloudPresets];

        void RenderCloudItems(string filter)
        {
            cloudListPanel.Children.Clear();
            IEnumerable<CloudPresetImageItem> sourceList;
            if (string.IsNullOrWhiteSpace(filter))
            {
                var recommended = DeviceImageService.FindRecommendedCloudPresets(device, allCloudItems);
                var recIds = recommended.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                sourceList = recommended.Concat(allCloudItems.Where(x => !recIds.Contains(x.Id))).Take(40);
            }
            else
            {
                sourceList = allCloudItems
                    .Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                x.Brand.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                x.MatchHint.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Take(40);
            }

            var recSet = DeviceImageService.FindRecommendedCloudPresets(device, allCloudItems)
                .Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in sourceList)
            {
                bool isRec = recSet.Contains(item.Id);
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = $"{(isRec ? "[推荐] " : "")}{item.Brand} · {item.Name}",
                    FontSize = 12,
                    FontWeight = isRec ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var applyBtn = new Button { Content = "下载并应用", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                applyBtn.Click += async (_, _) =>
                {
                    applyBtn.IsEnabled = false;
                    statusTb.Text = $"正在从云端拉取 {item.Name}...";
                    string? cached = await DeviceImageService.DownloadAndCacheCloudImageAsync(item);
                    applyBtn.IsEnabled = true;
                    if (cached != null)
                    {
                        App.Settings.Current.DeviceImageOverrides[device.Id] = cached;
                        App.Settings.Save();
                        previewImg.Source = DeviceImageService.CreateSourceFromFile(cached, 72);
                        changed = true;
                        statusTb.Text = $"已缓存并应用云端图片：{item.Name}";
                    }
                    else
                    {
                        statusTb.Text = $"云端拉取失败，请检查网络或尝试直接上传本地图片。";
                    }
                };
                Grid.SetColumn(applyBtn, 1);
                row.Children.Add(applyBtn);
                cloudListPanel.Children.Add(row);
            }
        }

        RenderCloudItems("");
        searchBox.TextChanged += (_, _) => RenderCloudItems(searchBox.Text);

        var urlRow = new Grid { ColumnSpacing = 8 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var customUrlBox = new TextBox { PlaceholderText = "或输入任意免费直链图片 URL（.png / .jpg / .svg）..." };
        urlRow.Children.Add(customUrlBox);
        var urlBtn = new Button { Content = "拉取此 URL" };
        urlBtn.Click += async (_, _) =>
        {
            urlBtn.IsEnabled = false;
            string? cached = await DeviceImageService.DownloadAndCacheUrlAsync(customUrlBox.Text.Trim());
            urlBtn.IsEnabled = true;
            if (cached != null)
            {
                App.Settings.Current.DeviceImageOverrides[device.Id] = cached;
                App.Settings.Save();
                previewImg.Source = DeviceImageService.CreateSourceFromFile(cached, 72);
                changed = true;
                statusTb.Text = "已缓存并应用自定义云端 URL 图片。";
            }
            else
            {
                statusTb.Text = "无法从此 URL 下载有效图片。";
            }
        };
        Grid.SetColumn(urlBtn, 1);
        urlRow.Children.Add(urlBtn);
        root.Children.Add(urlRow);

        _ = Task.Run(async () =>
        {
            var fetched = await DeviceImageService.FetchCloudCatalogAsync();
            App.UiQueue?.TryEnqueue(() =>
            {
                allCloudItems = fetched;
                RenderCloudItems(searchBox.Text);
            });
        });

        dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"选择「{device.Name}」的展示图片",
            Content = root,
            CloseButtonText = "完成",
        };
        await dialog.ShowAsync();
        return changed;
    }
}
