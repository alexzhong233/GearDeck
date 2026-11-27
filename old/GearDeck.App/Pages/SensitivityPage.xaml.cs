using System.Globalization;
using GearDeck.App.Services;
using GearDeck.Core.Models;
using GearDeck.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace GearDeck.App.Pages;

public sealed partial class SensitivityPage : Page
{
    private bool _binding = true;
    private SensitivityConversionMethod _method = SensitivityConversionMethod.Cm360;
    private string _categoryFilter = "全部";
    private SensitivityConversionResult? _lastResult;

    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public SensitivityPage()
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            App.Settings.Save();
        };

        Loaded += (_, _) =>
        {
            LoadPreferences();
            ApplyCategoryStyle();
            RenderGameCards();
        };
        Unloaded += (_, _) =>
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                App.Settings.Save();
            }
        };
    }

    // ── 初始化 / 偏好恢复 ────────────────────────────────────────────────────

    private static SensitivityConverterPrefs Prefs()
    {
        var p = App.Settings.Current.SensitivityPrefs ??= new();
        p.PerGameFovs ??= new(StringComparer.OrdinalIgnoreCase);
        return p;
    }

    private void LoadPreferences()
    {
        var p = Prefs();
        _method = Enum.IsDefined(typeof(SensitivityConversionMethod), p.ConversionMethod)
            ? (SensitivityConversionMethod)p.ConversionMethod
            : SensitivityConversionMethod.Cm360;
        p.ConversionMethod = (int)_method;

        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        var targetGame = SensitivityConverter.FindById(p.TargetGameId);
        p.BaseGameId = baseGame.Id;
        p.TargetGameId = targetGame.Id;

        WithBinding(() =>
        {
            FillGameCombo(BaseGameCombo, p.BaseGameId);

            BaseSensBox.Text = FormatNumber(SanitizeBaseSensitivity(baseGame, p.BaseSensitivity));
            BaseFovBox.Text = FormatNumber(ResolveFov(baseGame, isBase: true));
            TargetFovBox.Text = FormatNumber(ResolveFov(targetGame, isBase: false));

            FillResolutionCombo(BaseResPresetCombo, p.BaseWidth, p.BaseHeight, p.BaseStretched);
            FillResolutionCombo(TargetResPresetCombo, p.TargetWidth, p.TargetHeight, p.TargetStretched);

            BaseDpiBox.Text = FormatNumber(PositiveOr(p.BaseDpi, 800));
            TargetDpiBox.Text = FormatNumber(PositiveOr(p.TargetDpi, p.BaseDpi));
            BaseWidthBox.Text = p.BaseWidth.ToString(CultureInfo.InvariantCulture);
            BaseHeightBox.Text = p.BaseHeight.ToString(CultureInfo.InvariantCulture);
            TargetWidthBox.Text = p.TargetWidth.ToString(CultureInfo.InvariantCulture);
            TargetHeightBox.Text = p.TargetHeight.ToString(CultureInfo.InvariantCulture);

            SameDpiCheck.IsChecked = p.SameDpi;
            SameResCheck.IsChecked = p.SameResolution;
            BaseStretchCheck.IsChecked = p.BaseStretched;
            TargetStretchCheck.IsChecked = p.TargetStretched;
            TargetDpiPanel.Visibility = p.SameDpi ? Visibility.Collapsed : Visibility.Visible;
            TargetResPanel.Visibility = p.SameResolution ? Visibility.Collapsed : Visibility.Visible;
        });
        _binding = false;

        ApplyMethodStyle();
        Recalculate();
    }

    private static void FillGameCombo(ComboBox combo, string selectedId)
    {
        string canonicalId = SensitivityConverter.FindById(selectedId).Id;
        combo.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var game in SensitivityConverter.AllGames)
        {
            var item = new ComboBoxItem
            {
                Tag = game.Id,
                Content = $"{game.Name} · {game.ShortTag}",
            };
            combo.Items.Add(item);
            if (string.Equals(game.Id, canonicalId, StringComparison.OrdinalIgnoreCase))
                selected = item;
        }
        combo.SelectedItem = selected ?? combo.Items.FirstOrDefault();
    }

    private void FillResolutionCombo(ComboBox combo, int width, int height, bool stretched)
    {
        combo.Items.Clear();
        var preset = SensitivityConverter.ResolutionPresets.FirstOrDefault(r =>
            r.Width == width && r.Height == height && r.StretchedTo16By9 == stretched);

        ComboBoxItem? selected = null;
        foreach (var candidate in SensitivityConverter.ResolutionPresets)
        {
            var item = new ComboBoxItem { Tag = candidate.Id, Content = candidate.Label };
            combo.Items.Add(item);
            if (preset != null && candidate.Id == preset.Id)
                selected = item;
        }

        var custom = new ComboBoxItem { Tag = "custom", Content = "自定义分辨率" };
        combo.Items.Add(custom);
        combo.SelectedItem = selected ?? custom;
    }

    private static void SelectGameCombo(ComboBox combo, string gameId)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, gameId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectResolutionCombo(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void WithBinding(Action action)
    {
        bool previous = _binding;
        _binding = true;
        try { action(); }
        finally { _binding = previous; }
    }

    // ── 换算与 UI 回填 ───────────────────────────────────────────────────────

    private SensitivityConversionRequest BuildRequest(string targetGameId)
    {
        var p = Prefs();
        bool sameDpi = p.SameDpi;
        bool sameRes = p.SameResolution;
        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        return new SensitivityConversionRequest(
            BaseGameId: baseGame.Id,
            BaseSensitivity: SanitizeBaseSensitivity(baseGame, p.BaseSensitivity),
            BaseFov: ResolveFov(baseGame, isBase: true),
            TargetGameId: targetGameId,
            TargetFov: ResolveFov(SensitivityConverter.FindById(targetGameId), isBase: false),
            BaseDpi: PositiveOr(p.BaseDpi, 800),
            TargetDpi: PositiveOr(sameDpi ? p.BaseDpi : p.TargetDpi, 800),
            BaseWidth: Math.Max(1, p.BaseWidth),
            BaseHeight: Math.Max(1, p.BaseHeight),
            BaseStretchedTo16By9: p.BaseStretched,
            TargetWidth: Math.Max(1, sameRes ? p.BaseWidth : p.TargetWidth),
            TargetHeight: Math.Max(1, sameRes ? p.BaseHeight : p.TargetHeight),
            TargetStretchedTo16By9: sameRes ? p.BaseStretched : p.TargetStretched,
            Method: _method);
    }

    private void Recalculate()
    {
        var p = Prefs();
        var request = BuildRequest(p.TargetGameId);
        var result = SensitivityConverter.Convert(request);
        _lastResult = result;

        TargetGameTitleText.Text = result.TargetGame.Name;
        TargetTagBadgeText.Text = result.TargetGame.ShortTag;
        PrimaryResultText.Text = result.FormattedSelected;

        (PrimaryMethodTagText.Text, MethodDescText.Text) = _method switch
        {
            SensitivityConversionMethod.FocalLengthMdv0 => (
                "MDV 0% · 焦距缩放",
                $"FOV 焦距缩放：目标 {result.FormattedFovScaled}（保持屏幕视觉角速度一致，适合跟枪）。"),
            SensitivityConversionMethod.MonitorDistanceH100 => (
                "MDH 100% · 水平边缘对齐",
                $"水平边缘对齐：目标 {result.FormattedMdh100}（按 HFOV 等比缩放，屏幕边缘落点一致）。"),
            _ => (
                "360° 等距 · 物理转身",
                $"360° 等距：目标 {result.FormattedCm360}（保持物理 cm/360° 一致，腰射通用）。"),
        };

        BaseFovMetaText.Text = $"基准 FOV {request.BaseFov:F0}° → 实际 HFOV {result.BaseActualHFov:F1}° / VFOV {result.BaseActualVFov:F1}°" +
            (result.BaseGame.FovLocked ? "（该游戏 FOV 固定锁定）" : "") +
            (result.BaseGame.PhysicalYawAffectedByFov ? "（该游戏 FOV 影响物理 cm/360°）" : "");
        TargetFovMetaText.Text = $"目标 FOV {request.TargetFov:F0}° → 实际 HFOV {result.TargetActualHFov:F1}° / VFOV {result.TargetActualVFov:F1}°" +
            (result.TargetGame.FovLocked ? "（该游戏 FOV 固定锁定）" : "") +
            (result.TargetGame.PhysicalYawAffectedByFov ? "（该游戏 FOV 影响物理 cm/360°）" : "");

        MetricCompareText.Text = $"等距 {result.FormattedCm360} · 焦距 {result.FormattedFovScaled} · 边缘 {result.FormattedMdh100}";
        MetricCm360Text.Text = $"{result.BaseCm360:F2} → {result.TargetCm360:F2} cm/360°\neDPI {result.BaseEdpi:F0} → {result.TargetEdpi:F0}";
        MetricFovText.Text = $"基准 {result.BaseActualHFov:F1}°/{result.BaseActualVFov:F1}°\n目标 {result.TargetActualHFov:F1}°/{result.TargetActualVFov:F1}°";
        MetricPixelSpeedText.Text = $"{result.PixelsPerCountAtTargetRes:F2} px/count\n2K 跑满手速 ≥ {result.MinSpeedCmPerSecFor2000Hz:F1} cm/s";

        GuideSettingPathText.Text = $"设置路径：{result.TargetGame.SettingPath}";
        GuideConfigTipText.Text = $"配置提示：{result.TargetGame.ConfigTip}" +
            (string.IsNullOrEmpty(result.TargetGame.FormulaSourceNote) ? "" : $"\n公式备注：{result.TargetGame.FormulaSourceNote}");
        GuideAdsTipText.Text = $"开镜与注意：{result.TargetGame.AdsAndPitfallTip}";

        if (string.IsNullOrEmpty(result.ConfigSnippet))
        {
            ConfigSnippetPanel.Visibility = Visibility.Collapsed;
            ConfigSnippetText.Text = "";
        }
        else
        {
            ConfigSnippetPanel.Visibility = Visibility.Visible;
            ConfigSnippetText.Text = result.ConfigSnippet;
        }

        DynamicWarningsPanel.Children.Clear();
        foreach (var warning in result.DynamicWarnings)
        {
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 255, 193, 7));
            DynamicWarningsPanel.Children.Add(new Border
            {
                Background = brush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = warning,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            });
        }

        ApplyFovLock();
        UpdateAdvancedSummary();
    }

    private void ApplyFovLock()
    {
        var p = Prefs();
        bool baseLocked = SensitivityConverter.FindById(p.BaseGameId).FovLocked;
        BaseFovBox.IsEnabled = !baseLocked;
        BaseFovResetBtn.IsEnabled = !baseLocked;

        bool targetLocked = SensitivityConverter.FindById(p.TargetGameId).FovLocked;
        TargetFovBox.IsEnabled = !targetLocked;
        TargetFovResetBtn.IsEnabled = !targetLocked;
    }

    private void UpdateAdvancedSummary()
    {
        var p = Prefs();
        double bd = PositiveOr(p.BaseDpi, 800);
        double td = p.SameDpi ? bd : PositiveOr(p.TargetDpi, bd);
        string dpiPart = p.SameDpi || Math.Abs(bd - td) < 1.0
            ? $"{FormatNumber(bd)} DPI"
            : $"{FormatNumber(bd)}→{FormatNumber(td)} DPI";

        int bw = Math.Max(1, p.BaseWidth);
        int bh = Math.Max(1, p.BaseHeight);
        int tw = p.SameResolution ? bw : Math.Max(1, p.TargetWidth);
        int th = p.SameResolution ? bh : Math.Max(1, p.TargetHeight);
        string resPart = p.SameResolution || (bw == tw && bh == th && p.BaseStretched == p.TargetStretched)
            ? $"{bw}×{bh} {AspectLabel(bw, bh)}"
            : $"{bw}×{bh}→{tw}×{th}";

        AdvancedSummaryText.Text = $"高级功能：DPI 与 分辨率（当前 {dpiPart} · {resPart}）";
    }

    private void ApplyMethodStyle()
    {
        SetAccent(MethodCm360Btn, _method == SensitivityConversionMethod.Cm360);
        SetAccent(MethodMdv0Btn, _method == SensitivityConversionMethod.FocalLengthMdv0);
        SetAccent(MethodMdh100Btn, _method == SensitivityConversionMethod.MonitorDistanceH100);
    }

    private void ApplyCategoryStyle()
    {
        foreach (var button in CategoryFilterPanel.Children.OfType<Button>())
            SetAccent(button, string.Equals(button.Tag as string, _categoryFilter, StringComparison.Ordinal));
    }

    private static void SetAccent(Button button, bool active)
    {
        button.Style = (Style)Application.Current.Resources[active ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    // ── 输入处理 ─────────────────────────────────────────────────────────────

    private void OnNumericInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_binding || sender is not TextBox box) return;
        var p = Prefs();

        if (ReferenceEquals(box, BaseSensBox))
        {
            bool allowZero = SensitivityConverter.FindById(p.BaseGameId).ScaleKind == SensitivityScaleKind.Affine;
            if (!(allowZero ? TryNonNegativeDouble(box.Text, out var v) : TryPositiveDouble(box.Text, out v)))
                return;
            p.BaseSensitivity = v;
        }
        else if (ReferenceEquals(box, BaseFovBox))
        {
            if (!TryPositiveDouble(box.Text, out var v)) return;
            p.BaseFov = v;
            RememberFov(p.BaseGameId, v);
        }
        else if (ReferenceEquals(box, TargetFovBox))
        {
            if (!TryPositiveDouble(box.Text, out var v)) return;
            p.TargetFov = v;
            RememberFov(p.TargetGameId, v);
        }
        else if (ReferenceEquals(box, BaseDpiBox))
        {
            if (!TryPositiveDouble(box.Text, out var v)) return;
            p.BaseDpi = v;
            if (p.SameDpi)
            {
                p.TargetDpi = v;
                WithBinding(() => TargetDpiBox.Text = FormatNumber(v));
            }
        }
        else if (ReferenceEquals(box, TargetDpiBox))
        {
            if (!TryPositiveDouble(box.Text, out var v)) return;
            p.TargetDpi = v;
        }
        else if (ReferenceEquals(box, BaseWidthBox))
        {
            if (!TryPositiveInt(box.Text, out var v)) return;
            p.BaseWidth = v;
            MarkCustom(BaseResPresetCombo);
            if (p.SameResolution)
            {
                p.TargetWidth = v;
                WithBinding(() =>
                {
                    TargetWidthBox.Text = v.ToString(CultureInfo.InvariantCulture);
                    SelectResolutionCombo(TargetResPresetCombo, "custom");
                });
            }
        }
        else if (ReferenceEquals(box, BaseHeightBox))
        {
            if (!TryPositiveInt(box.Text, out var v)) return;
            p.BaseHeight = v;
            MarkCustom(BaseResPresetCombo);
            if (p.SameResolution)
            {
                p.TargetHeight = v;
                WithBinding(() =>
                {
                    TargetHeightBox.Text = v.ToString(CultureInfo.InvariantCulture);
                    SelectResolutionCombo(TargetResPresetCombo, "custom");
                });
            }
        }
        else if (ReferenceEquals(box, TargetWidthBox))
        {
            if (!TryPositiveInt(box.Text, out var v)) return;
            p.TargetWidth = v;
            MarkCustom(TargetResPresetCombo);
        }
        else if (ReferenceEquals(box, TargetHeightBox))
        {
            if (!TryPositiveInt(box.Text, out var v)) return;
            p.TargetHeight = v;
            MarkCustom(TargetResPresetCombo);
        }
        else
        {
            return;
        }

        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void MarkCustom(ComboBox combo) => WithBinding(() => SelectResolutionCombo(combo, "custom"));

    private void OnBaseGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;

        SetBaseGame(id);
    }

    private void SetBaseGame(string id)
    {
        var p = Prefs();
        var game = SensitivityConverter.FindById(id);
        p.BaseGameId = game.Id;
        double fov = ResolveFov(game, isBase: true);
        p.BaseFov = fov;
        WithBinding(() =>
        {
            SelectGameCombo(BaseGameCombo, game.Id);
            BaseFovBox.Text = FormatNumber(fov);
        });
        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnBaseSensHalfClick(object sender, RoutedEventArgs e) => ScaleBaseSensitivity(0.5);

    private void OnBaseSensDoubleClick(object sender, RoutedEventArgs e) => ScaleBaseSensitivity(2.0);

    private void ScaleBaseSensitivity(double factor)
    {
        var p = Prefs();
        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        double current = SanitizeBaseSensitivity(baseGame, p.BaseSensitivity);
        double next;
        if (baseGame.ScaleKind == SensitivityScaleKind.PubgExponential)
        {
            next = Math.Max(0.0, current + 50.0 * Math.Log10(factor));
        }
        else if (baseGame.ScaleKind == SensitivityScaleKind.Affine)
        {
            int bw = Math.Max(1, p.BaseWidth);
            int bh = Math.Max(1, p.BaseHeight);
            var (baseHFov, _, _) = SensitivityConverter.ComputeActualFov(
                baseGame.FovBasis, ResolveFov(baseGame, isBase: true), bw, bh, p.BaseStretched);
            double scaledDeg = SensitivityConverter.DegreesPerCountFromSens(baseGame, current, baseHFov) * factor;
            next = SensitivityConverter.SensFromDegreesPerCount(baseGame, scaledDeg, baseHFov);
        }
        else
        {
            next = current * factor;
        }

        p.BaseSensitivity = next;
        WithBinding(() => BaseSensBox.Text = FormatNumber(next));
        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnBaseFovResetClick(object sender, RoutedEventArgs e)
    {
        var p = Prefs();
        ResetFov(BaseFovBox, p.BaseGameId, isBase: true);
    }

    private void OnTargetFovResetClick(object sender, RoutedEventArgs e)
    {
        var p = Prefs();
        ResetFov(TargetFovBox, p.TargetGameId, isBase: false);
    }

    private void ResetFov(TextBox box, string gameId, bool isBase)
    {
        var game = SensitivityConverter.FindById(gameId);
        WithBinding(() => box.Text = FormatNumber(game.DefaultFov));
        Prefs().PerGameFovs.Remove(game.Id);
        if (isBase) Prefs().BaseFov = game.DefaultFov;
        else Prefs().TargetFov = game.DefaultFov;
        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnSwapGamesClick(object sender, RoutedEventArgs e)
    {
        var p = Prefs();
        string oldBase = p.BaseGameId;
        string oldTarget = p.TargetGameId;
        string baseFovText = BaseFovBox.Text;
        string targetFovText = TargetFovBox.Text;
        double? convertedTargetSens = _lastResult?.TargetSensitivitySelected;

        p.BaseGameId = oldTarget;
        p.TargetGameId = oldBase;

        var newBaseGame = SensitivityConverter.FindById(oldTarget);
        if (convertedTargetSens is { } nextSens && double.IsFinite(nextSens))
        {
            p.BaseSensitivity = newBaseGame.ScaleKind == SensitivityScaleKind.Affine
                ? Math.Max(0.0, nextSens)
                : (nextSens > 0 ? nextSens : PositiveOr(p.BaseSensitivity, 1.0));
        }

        WithBinding(() =>
        {
            SelectGameCombo(BaseGameCombo, oldTarget);
            BaseSensBox.Text = FormatNumber(SanitizeBaseSensitivity(newBaseGame, p.BaseSensitivity));
            BaseFovBox.Text = targetFovText;
            TargetFovBox.Text = baseFovText;
            if (TryPositiveDouble(targetFovText, out var bf))
            {
                p.BaseFov = bf;
                RememberFov(oldTarget, bf);
            }
            if (TryPositiveDouble(baseFovText, out var tf))
            {
                p.TargetFov = tf;
                RememberFov(oldBase, tf);
            }

            if (!p.SameDpi)
            {
                (p.BaseDpi, p.TargetDpi) = (p.TargetDpi, p.BaseDpi);
                BaseDpiBox.Text = FormatNumber(PositiveOr(p.BaseDpi, 800));
                TargetDpiBox.Text = FormatNumber(PositiveOr(p.TargetDpi, 800));
            }

            if (!p.SameResolution)
            {
                (p.BaseWidth, p.TargetWidth) = (p.TargetWidth, p.BaseWidth);
                (p.BaseHeight, p.TargetHeight) = (p.TargetHeight, p.BaseHeight);
                (p.BaseStretched, p.TargetStretched) = (p.TargetStretched, p.BaseStretched);
                BaseWidthBox.Text = p.BaseWidth.ToString(CultureInfo.InvariantCulture);
                BaseHeightBox.Text = p.BaseHeight.ToString(CultureInfo.InvariantCulture);
                TargetWidthBox.Text = p.TargetWidth.ToString(CultureInfo.InvariantCulture);
                TargetHeightBox.Text = p.TargetHeight.ToString(CultureInfo.InvariantCulture);
                BaseStretchCheck.IsChecked = p.BaseStretched;
                TargetStretchCheck.IsChecked = p.TargetStretched;
                FillResolutionCombo(BaseResPresetCombo, p.BaseWidth, p.BaseHeight, p.BaseStretched);
                FillResolutionCombo(TargetResPresetCombo, p.TargetWidth, p.TargetHeight, p.TargetStretched);
            }
        });

        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnMethodClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _method = (button.Tag as string) switch
        {
            "Mdv0" => SensitivityConversionMethod.FocalLengthMdv0,
            "Mdh100" => SensitivityConversionMethod.MonitorDistanceH100,
            _ => SensitivityConversionMethod.Cm360,
        };
        Prefs().ConversionMethod = (int)_method;
        ApplyMethodStyle();
        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnAdvancedCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        var p = Prefs();

        p.BaseStretched = BaseStretchCheck.IsChecked == true;
        p.TargetStretched = TargetStretchCheck.IsChecked == true;

        p.SameDpi = SameDpiCheck.IsChecked == true;
        TargetDpiPanel.Visibility = p.SameDpi ? Visibility.Collapsed : Visibility.Visible;
        if (p.SameDpi)
        {
            p.TargetDpi = p.BaseDpi;
            WithBinding(() => TargetDpiBox.Text = FormatNumber(p.BaseDpi));
        }

        p.SameResolution = SameResCheck.IsChecked == true;
        TargetResPanel.Visibility = p.SameResolution ? Visibility.Collapsed : Visibility.Visible;
        if (p.SameResolution)
        {
            p.TargetWidth = p.BaseWidth;
            p.TargetHeight = p.BaseHeight;
            p.TargetStretched = p.BaseStretched;
            WithBinding(() =>
            {
                TargetWidthBox.Text = p.BaseWidth.ToString(CultureInfo.InvariantCulture);
                TargetHeightBox.Text = p.BaseHeight.ToString(CultureInfo.InvariantCulture);
                TargetStretchCheck.IsChecked = p.BaseStretched;
                FillResolutionCombo(TargetResPresetCombo, p.BaseWidth, p.BaseHeight, p.BaseStretched);
            });
        }

        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    private void OnBaseDpiPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag) BaseDpiBox.Text = tag;
    }

    private void OnTargetDpiPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag) TargetDpiBox.Text = tag;
    }

    private void OnBaseResPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ApplyResolutionPreset(combo, item, tag, isBase: true);
    }

    private void OnTargetResPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ApplyResolutionPreset(combo, item, tag, isBase: false);
    }

    private void ApplyResolutionPreset(ComboBox combo, ComboBoxItem item, string tag, bool isBase)
    {
        var p = Prefs();
        var preset = SensitivityConverter.ResolutionPresets.FirstOrDefault(r => r.Id == tag);
        if (preset != null)
        {
            WithBinding(() =>
            {
                (isBase ? BaseWidthBox : TargetWidthBox).Text = preset.Width.ToString(CultureInfo.InvariantCulture);
                (isBase ? BaseHeightBox : TargetHeightBox).Text = preset.Height.ToString(CultureInfo.InvariantCulture);
                if (isBase)
                {
                    p.BaseWidth = preset.Width;
                    p.BaseHeight = preset.Height;
                    BaseStretchCheck.IsChecked = preset.StretchedTo16By9;
                    p.BaseStretched = preset.StretchedTo16By9;
                    if (p.SameResolution)
                    {
                        p.TargetWidth = preset.Width;
                        p.TargetHeight = preset.Height;
                        p.TargetStretched = preset.StretchedTo16By9;
                        TargetWidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
                        TargetHeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
                        TargetStretchCheck.IsChecked = preset.StretchedTo16By9;
                        SelectResolutionCombo(TargetResPresetCombo, preset.Id);
                    }
                }
                else
                {
                    p.TargetWidth = preset.Width;
                    p.TargetHeight = preset.Height;
                    TargetStretchCheck.IsChecked = preset.StretchedTo16By9;
                    p.TargetStretched = preset.StretchedTo16By9;
                }
            });
        }
        else if (item.Content is string label && label == "自定义分辨率")
        {
            // 保持输入框当前数值，等待用户手动输入。
        }

        Recalculate();
        ScheduleSave();
        RenderGameCards();
    }

    // ── 鼠标 DPI 读取与复制 ──────────────────────────────────────────────────

    private async void OnReadMouseDpiClick(object sender, RoutedEventArgs e)
    {
        var mice = App.LastDevices.Where(d => d.Kind == DeviceKind.Mouse && !d.IsSleeping).ToList();
        if (mice.Count == 0)
            mice = App.LastDevices.Where(d => d.Kind == DeviceKind.Mouse).ToList();
        if (mice.Count == 0)
        {
            ShowInfo("未检测到鼠标设备，请连接或唤醒鼠标后再试。", InfoBarSeverity.Warning);
            return;
        }

        ReadMouseDpiBtn.IsEnabled = false;
        try
        {
            foreach (var mouse in mice)
            {
                var caps = await App.Devices.QueryCapabilitiesAsync(mouse.Id);
                if (caps.IsOnline && caps.SupportsDpi && caps.CurrentDpiX > 0)
                {
                    BaseDpiBox.Text = caps.CurrentDpiX.ToString(CultureInfo.InvariantCulture);
                    ShowInfo($"已读取 {mouse.Name} 的当前 DPI：{caps.CurrentDpiX}。", InfoBarSeverity.Success);
                    return;
                }
            }
            ShowInfo("暂时读取不到鼠标 DPI，请先唤醒鼠标（移动一下）后再试。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowInfo($"读取鼠标 DPI 失败：{ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            ReadMouseDpiBtn.IsEnabled = true;
        }
    }

    private void OnCopySensClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult == null) return;
        CopyToClipboard(_lastResult.FormattedSelected, $"已复制灵敏度 {_lastResult.FormattedSelected}。");
    }

    private void OnCopySnippetClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.ConfigSnippet is not string snippet) return;
        CopyToClipboard(snippet, "已复制配置命令。");
    }

    private void CopyToClipboard(string text, string message)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowInfo(message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"复制失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    // ── 游戏卡片墙 ───────────────────────────────────────────────────────────

    private void OnGameFilterChanged(object sender, TextChangedEventArgs e) => RenderGameCards();

    private void OnCategoryFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        _categoryFilter = tag;
        ApplyCategoryStyle();
        RenderGameCards();
    }

    private void RenderGameCards()
    {
        var p = Prefs();
        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        var targetGame = SensitivityConverter.FindById(p.TargetGameId);
        string query = GameSearchBox.Text?.Trim() ?? "";

        var cards = new List<UIElement>();
        foreach (var game in SensitivityConverter.AllGames)
        {
            if (_categoryFilter != "全部" &&
                !string.Equals(game.Category, _categoryFilter, StringComparison.Ordinal))
                continue;

            if (query.Length > 0 &&
                !game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.ShortTag.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.Category.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isTarget = string.Equals(game.Id, targetGame.Id, StringComparison.OrdinalIgnoreCase);
            bool isBase = string.Equals(game.Id, baseGame.Id, StringComparison.OrdinalIgnoreCase);
            var preview = SensitivityConverter.Convert(BuildRequest(game.Id));

            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(new TextBlock
            {
                Text = game.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{game.ShortTag} · {game.Category}",
                FontSize = 11,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            });
            content.Children.Add(new TextBlock
            {
                Text = $"≈ {preview.FormattedSelected}",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
            });
            if (isTarget || isBase)
            {
                content.Children.Add(new TextBlock
                {
                    Text = isTarget && isBase ? "● 当前基准 / 目标" : isTarget ? "● 当前目标" : "● 当前基准",
                    FontSize = 11,
                    Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                });
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = "点击卡片换算到该游戏",
                    FontSize = 11,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                });
            }

            var setBaseBtn = new Button
            {
                Content = "设为基准",
                Tag = game.Id,
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            setBaseBtn.Click += OnSetBaseGameClick;
            setBaseBtn.Tapped += (_, args) => args.Handled = true;
            content.Children.Add(setBaseBtn);

            var card = new Border
            {
                Tag = game.Id,
                Child = content,
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush(isTarget ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
                BorderThickness = isTarget ? new Thickness(2) : new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
            };
            card.Tapped += OnGameCardTapped;
            cards.Add(card);
        }

        GameCardsHost.SetItems(cards);
    }

    private void OnGameCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border card && card.Tag is string id)
            SetTargetGame(id);
    }

    private void OnSetBaseGameClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
            SetBaseGame(id);
    }

    private void SetTargetGame(string id)
    {
        var p = Prefs();
        var game = SensitivityConverter.FindById(id);
        p.TargetGameId = game.Id;
        double fov = ResolveFov(game, isBase: false);
        p.TargetFov = fov;
        WithBinding(() => TargetFovBox.Text = FormatNumber(fov));
        Recalculate();
        ScheduleSave();
        RenderGameCards();
        RootScrollViewer.ChangeView(null, 0.0, null, true);
    }

    // ── 响应式布局 ───────────────────────────────────────────────────────────

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TargetCard == null || BaseColDef == null || WorkspaceGrid.ColumnDefinitions.Count < 2)
            return;
        if (e.NewSize.Width < 880)
        {
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            BaseColDef.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(TargetCard, 1);
            Grid.SetColumn(TargetCard, 0);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            BaseColDef.Width = new GridLength(370);
            Grid.SetRow(TargetCard, 0);
            Grid.SetColumn(TargetCard, 1);
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private double ResolveFov(GameSensitivityProfile game, bool isBase)
    {
        if (game.FovLocked) return game.DefaultFov;
        var p = Prefs();
        if (p.PerGameFovs.TryGetValue(game.Id, out var saved) && saved > 0)
            return saved;

        double stored = isBase ? p.BaseFov : p.TargetFov;
        string storedId = isBase ? p.BaseGameId : p.TargetGameId;
        return stored > 0 && string.Equals(storedId, game.Id, StringComparison.OrdinalIgnoreCase)
            ? stored
            : game.DefaultFov;
    }

    private void RememberFov(string gameId, double value)
        => Prefs().PerGameFovs[gameId] = value;

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static string FormatNumber(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double SanitizeBaseSensitivity(GameSensitivityProfile game, double value)
        => game.ScaleKind == SensitivityScaleKind.Affine
            ? (double.IsFinite(value) && value >= 0 ? value : 1.0)
            : PositiveOr(value, 1.0);

    private static double PositiveOr(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;

    private static bool TryPositiveDouble(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0;

    private static bool TryNonNegativeDouble(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;

    private static bool TryPositiveInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string AspectLabel(int width, int height)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        int a = w, b = h;
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        w /= a;
        h /= a;
        return (w, h) switch
        {
            (16, 9) => "16:9",
            (4, 3) => "4:3",
            (21, 9) => "21:9",
            (16, 10) => "16:10",
            _ => $"{w}:{h}",
        };
    }
}
