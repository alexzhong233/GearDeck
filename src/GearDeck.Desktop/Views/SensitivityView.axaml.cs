using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using GearDeck.Core.Models;
using GearDeck.Core.Services;

namespace GearDeck.Desktop.Views;

public partial class SensitivityView : UserControl
{
    private bool _binding = true;
    private bool _gamesExpanded;
    private bool _gameCardsDirty = true;
    private SensitivityConversionResult? _lastResult;
    private UniformGrid? _gameCardsGrid;
    private readonly DispatcherTimer _saveTimer;

    public SensitivityView()
    {
        _binding = true;
        InitializeComponent();
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, OnSaveTimerTick);
        WireEvents();
        LoadPreferences();
        SetGamesExpanded(false);
        _binding = false;
        Unloaded += OnUnloaded;
    }

    // ── 事件接线 ─────────────────────────────────────────────────────────────

    private void WireEvents()
    {
        BaseSensBox.TextChanged += OnNumericInputChanged;
        BaseFovBox.TextChanged += OnNumericInputChanged;
        TargetFovBox.TextChanged += OnNumericInputChanged;
        BaseDpiBox.TextChanged += OnNumericInputChanged;
        TargetDpiBox.TextChanged += OnNumericInputChanged;
        BaseWidthBox.TextChanged += OnNumericInputChanged;
        BaseHeightBox.TextChanged += OnNumericInputChanged;
        TargetWidthBox.TextChanged += OnNumericInputChanged;
        TargetHeightBox.TextChanged += OnNumericInputChanged;

        BaseResPresetCombo.SelectionChanged += OnBaseResPresetChanged;
        TargetResPresetCombo.SelectionChanged += OnTargetResPresetChanged;

        BaseSensHalfBtn.Click += (_, _) => { if (!_binding) ScaleBaseSensitivity(0.5); };
        BaseSensDoubleBtn.Click += (_, _) => { if (!_binding) ScaleBaseSensitivity(2.0); };
        BaseFovResetBtn.Click += (_, _) => { if (!_binding) ResetFov(BaseFovBox, CurrentBaseId(), isBase: true); };
        TargetFovResetBtn.Click += (_, _) => { if (!_binding) ResetFov(TargetFovBox, CurrentTargetId(), isBase: false); };
        SwapGamesBtn.Click += OnSwapGamesClick;
        CopyCm360Btn.Click += (_, _) => CopyConversionValue(_lastResult?.FormattedCm360);
        CopyMdv0Btn.Click += (_, _) => CopyConversionValue(_lastResult?.FormattedFovScaled);
        CopyMdh100Btn.Click += (_, _) => CopyConversionValue(_lastResult?.FormattedMdh100);
        CopySnippetBtn.Click += OnCopySnippetClick;
        ReadMouseDpiBtn.Click += OnReadMouseDpiClick;

        SameDpiCheck.IsCheckedChanged += OnAdvancedCheckChanged;
        SameResCheck.IsCheckedChanged += OnAdvancedCheckChanged;
        BaseStretchCheck.IsCheckedChanged += OnAdvancedCheckChanged;
        TargetStretchCheck.IsCheckedChanged += OnAdvancedCheckChanged;

        var baseDpiWrap = (WrapPanel)BaseDpiBox.Parent!;
        foreach (var button in baseDpiWrap.Children.OfType<Button>())
            button.Click += (_, _) => { if (!_binding && button.Tag is string tag) BaseDpiBox.Text = tag; };
        var targetDpiWrap = (WrapPanel)TargetDpiBox.Parent!;
        foreach (var button in targetDpiWrap.Children.OfType<Button>())
            button.Click += (_, _) => { if (!_binding && button.Tag is string tag) TargetDpiBox.Text = tag; };

        ToggleGamesBtn.Click += (_, _) => SetGamesExpanded(!_gamesExpanded);
        GameSearchBox.TextChanged += (_, _) =>
        {
            if (_binding) return;
            if (!string.IsNullOrWhiteSpace(GameSearchBox.Text) && !_gamesExpanded)
            {
                _gameCardsDirty = true;
                SetGamesExpanded(true);
            }
            else if (_gamesExpanded)
                RenderGameCards();
            else
                _gameCardsDirty = true;
        };
        GameCardsHost.SizeChanged += (_, _) => UpdateGameCardColumns();
        WorkspaceGrid.SizeChanged += OnWorkspaceSizeChanged;
    }

    private static string CurrentBaseId()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        return SensitivityConverter.FindById(App.Settings.Current.SensitivityPrefs.BaseGameId).Id;
    }

    private static string CurrentTargetId()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        return SensitivityConverter.FindById(App.Settings.Current.SensitivityPrefs.TargetGameId).Id;
    }

    // ── 初始化 / 偏好恢复 ────────────────────────────────────────────────────

    private void LoadPreferences()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        p.PerGameFovs ??= new(StringComparer.OrdinalIgnoreCase);

        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        var targetGame = SensitivityConverter.FindById(p.TargetGameId);
        p.BaseGameId = baseGame.Id;
        p.TargetGameId = targetGame.Id;

        WithBinding(() =>
        {
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
            TargetDpiPanel.IsVisible = !p.SameDpi;
            TargetResPanel.IsVisible = !p.SameResolution;
        });

        Recalculate();
    }

    private static void FillResolutionCombo(ComboBox combo, int width, int height, bool stretched)
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
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
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
            Method: SensitivityConversionMethod.Cm360);
    }

    private void Recalculate()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        var request = BuildRequest(p.TargetGameId);
        var result = SensitivityConverter.Convert(request);
        _lastResult = result;

        BaseGameTitleText.Text = result.BaseGame.Name;
        BaseTagBadgeText.Text = result.BaseGame.ShortTag;
        GameSummaryText.Text = $"基准：{result.BaseGame.Name}  →  目标：{result.TargetGame.Name}";
        TargetGameTitleText.Text = result.TargetGame.Name;
        TargetTagBadgeText.Text = result.TargetGame.ShortTag;

        ResultCm360Text.Text = result.FormattedCm360;
        ResultCm360SubText.Text = $"{result.TargetCm360:F2} cm/360°";
        ResultMdv0Text.Text = result.FormattedFovScaled;
        ResultMdh100Text.Text = result.FormattedMdh100;

        BaseFovMetaText.Text = $"→ 实际 HFOV {result.BaseActualHFov:F1}° / VFOV {result.BaseActualVFov:F1}°" +
            (result.BaseGame.FovLocked ? "（固定锁定）" : "") +
            (result.BaseGame.PhysicalYawAffectedByFov ? "（影响 cm/360°）" : "");
        TargetFovMetaText.Text = $"→ 实际 HFOV {result.TargetActualHFov:F1}° / VFOV {result.TargetActualVFov:F1}°" +
            (result.TargetGame.FovLocked ? "（固定锁定）" : "") +
            (result.TargetGame.PhysicalYawAffectedByFov ? "（影响 cm/360°）" : "");

        double minSpeed1k = result.MinSpeedCmPerSecFor2000Hz / 2.0;
        MetricPhysicalText.Text =
            $"物理转身：{result.BaseCm360:F2} → {result.TargetCm360:F2} cm/360°  ·  eDPI：{result.BaseEdpi:F0} → {result.TargetEdpi:F0}\n" +
            $"像素步进：{result.PixelsPerCountAtTargetRes:F2} px/count\n" +
            $"回报率跑满手速：1K ≥ {minSpeed1k:F1} cm/s  ·  2K ≥ {result.MinSpeedCmPerSecFor2000Hz:F1} cm/s";

        GuideSettingPathText.Text = $"设置路径：{result.TargetGame.SettingPath}";
        GuideConfigTipText.Text = $"配置提示：{result.TargetGame.ConfigTip}" +
            (string.IsNullOrEmpty(result.TargetGame.FormulaSourceNote) ? "" : $"\n公式备注：{result.TargetGame.FormulaSourceNote}");
        GuideAdsTipText.Text = $"开镜与注意：{result.TargetGame.AdsAndPitfallTip}";

        if (string.IsNullOrEmpty(result.ConfigSnippet))
        {
            ConfigSnippetPanel.IsVisible = false;
            ConfigSnippetText.Text = "";
        }
        else
        {
            ConfigSnippetPanel.IsVisible = true;
            ConfigSnippetText.Text = result.ConfigSnippet;
        }

        DynamicWarningsPanel.Children.Clear();
        foreach (var warning in result.DynamicWarnings)
        {
            var brush = new SolidColorBrush(Color.FromArgb(48, 255, 193, 7));
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
                    Foreground = Brushes.Gray,
                },
            });
        }

        ApplyFovLock();
    }

    private void ApplyFovLock()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        bool baseLocked = SensitivityConverter.FindById(p.BaseGameId).FovLocked;
        BaseFovBox.IsEnabled = !baseLocked;
        BaseFovResetBtn.IsEnabled = !baseLocked;

        bool targetLocked = SensitivityConverter.FindById(p.TargetGameId).FovLocked;
        TargetFovBox.IsEnabled = !targetLocked;
        TargetFovResetBtn.IsEnabled = !targetLocked;
    }

    // ── 输入处理 ─────────────────────────────────────────────────────────────

    private void OnNumericInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (_binding || sender is not TextBox box) return;
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;

        if (ReferenceEquals(box, BaseSensBox))
        {
            // 战地仿射曲线允许输入 0，其余游戏必须为正数。
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
        MarkGameCardsDirtyOrRender();
    }

    private void MarkCustom(ComboBox combo) => WithBinding(() => SelectResolutionCombo(combo, "custom"));

    private void SetBaseGame(string id)
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        var game = SensitivityConverter.FindById(id);
        p.BaseGameId = game.Id;
        double fov = ResolveFov(game, isBase: true);
        p.BaseFov = fov;
        WithBinding(() =>
        {
            BaseSensBox.Text = FormatNumber(SanitizeBaseSensitivity(game, p.BaseSensitivity));
            BaseFovBox.Text = FormatNumber(fov);
        });
        Recalculate();
        ScheduleSave();
        MarkGameCardsDirtyOrRender();
    }

    private void ScaleBaseSensitivity(double factor)
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
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
        MarkGameCardsDirtyOrRender();
    }

    private void ResetFov(TextBox box, string gameId, bool isBase)
    {
        var game = SensitivityConverter.FindById(gameId);
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        p.PerGameFovs ??= new(StringComparer.OrdinalIgnoreCase);
        WithBinding(() => box.Text = FormatNumber(game.DefaultFov));
        p.PerGameFovs.Remove(game.Id);
        if (isBase) p.BaseFov = game.DefaultFov;
        else p.TargetFov = game.DefaultFov;
        Recalculate();
        ScheduleSave();
        MarkGameCardsDirtyOrRender();
    }

    private void OnSwapGamesClick(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        string oldBase = p.BaseGameId;
        string oldTarget = p.TargetGameId;
        string baseFovText = BaseFovBox.Text ?? string.Empty;
        string targetFovText = TargetFovBox.Text ?? string.Empty;
        double? convertedTargetSens = _lastResult?.TargetSensitivityCm360;

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
        MarkGameCardsDirtyOrRender();
    }

    private void OnAdvancedCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_binding) return;
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;

        p.BaseStretched = BaseStretchCheck.IsChecked == true;
        p.TargetStretched = TargetStretchCheck.IsChecked == true;

        p.SameDpi = SameDpiCheck.IsChecked == true;
        TargetDpiPanel.IsVisible = !p.SameDpi;
        if (p.SameDpi)
        {
            p.TargetDpi = p.BaseDpi;
            WithBinding(() => TargetDpiBox.Text = FormatNumber(p.BaseDpi));
        }

        p.SameResolution = SameResCheck.IsChecked == true;
        TargetResPanel.IsVisible = !p.SameResolution;
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
        MarkGameCardsDirtyOrRender();
    }

    private void OnBaseResPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ApplyResolutionPreset(item, tag, isBase: true);
    }

    private void OnTargetResPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ApplyResolutionPreset(item, tag, isBase: false);
    }

    private void ApplyResolutionPreset(ComboBoxItem item, string tag, bool isBase)
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
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
            // 保留输入框当前数值，等待用户手动输入。
        }

        Recalculate();
        ScheduleSave();
        MarkGameCardsDirtyOrRender();
    }

    // ── 鼠标 DPI 读取与复制 ──────────────────────────────────────────────────

    private async void OnReadMouseDpiClick(object? sender, RoutedEventArgs e)
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

    private void CopyConversionValue(string? formatted)
    {
        if (string.IsNullOrWhiteSpace(formatted)) return;
        _ = CopyToClipboard(formatted, $"已复制灵敏度 {formatted}。");
    }

    private void OnCopySnippetClick(object? sender, RoutedEventArgs e)
    {
        if (_lastResult?.ConfigSnippet is not string snippet) return;
        _ = CopyToClipboard(snippet, "已复制配置命令。");
    }

    private async Task CopyToClipboard(string text, string message)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                ShowInfo("剪贴板不可用。", InfoBarSeverity.Warning);
                return;
            }
            await clipboard.SetTextAsync(text);
            ShowInfo(message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo($"复制失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            MainWindow.HideToast();
        else
            MainWindow.ShowToast(message, severity);
    }

    // ── 游戏卡片墙（顶部可收缩） ───────────────────────────────────────────────

    private void SetGamesExpanded(bool expanded)
    {
        _gamesExpanded = expanded;
        GameCardsContainer.IsVisible = expanded;
        ToggleGamesBtn.Content = expanded ? "收起 ▲" : "展开选择 ▼";
        if (expanded && _gameCardsDirty)
            RenderGameCards();
    }

    private void MarkGameCardsDirtyOrRender()
    {
        if (_gamesExpanded)
            RenderGameCards();
        else
            _gameCardsDirty = true;
    }

    private int ComputeGameCardColumns()
    {
        double w = GameCardsHost.Bounds.Width;
        if (w <= 1) return 3;
        return w >= 1060 ? 4 : (w >= 760 ? 3 : (w >= 480 ? 2 : 1));
    }

    private void UpdateGameCardColumns()
    {
        if (_gameCardsGrid == null) return;
        int cols = ComputeGameCardColumns();
        if (_gameCardsGrid.Columns != cols)
            _gameCardsGrid.Columns = cols;
    }

    private void RenderGameCards()
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        var baseGame = SensitivityConverter.FindById(p.BaseGameId);
        var targetGame = SensitivityConverter.FindById(p.TargetGameId);
        string query = GameSearchBox.Text?.Trim() ?? "";

        GameCardsHost.Children.Clear();
        _gameCardsGrid = new UniformGrid
        {
            Columns = ComputeGameCardColumns(),
            VerticalAlignment = VerticalAlignment.Top,
        };

        IBrush cardBg = ResolveBrush("SubtleFillColorSecondaryBrush");
        IBrush defaultStroke = ResolveBrush("CardStrokeColorDefaultBrush");
        IBrush accentStroke = ResolveBrush("AccentFillColorDefaultBrush");
        IBrush secondaryText = ResolveBrush("TextFillColorSecondaryBrush");

        foreach (var game in SensitivityConverter.AllGames)
        {
            if (query.Length > 0 &&
                !game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.ShortTag.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.Category.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !game.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isTarget = string.Equals(game.Id, targetGame.Id, StringComparison.OrdinalIgnoreCase);
            bool isBase = string.Equals(game.Id, baseGame.Id, StringComparison.OrdinalIgnoreCase);
            var preview = SensitivityConverter.Convert(BuildRequest(game.Id));

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = game.Name,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{game.ShortTag} · {game.Category}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = secondaryText,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"≈ {preview.FormattedSelected}",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
            });

            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            actions.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var setBaseBtn = new Button
            {
                Content = isBase ? "当前基准" : "设为基准",
                Tag = game.Id,
                FontSize = 11,
                Padding = new Thickness(6, 4),
                Margin = new Thickness(0, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (isBase)
                setBaseBtn.Classes.Add("accent");
            setBaseBtn.Click += OnSetBaseGameClick;
            Grid.SetColumn(setBaseBtn, 0);
            actions.Children.Add(setBaseBtn);

            var setTargetBtn = new Button
            {
                Content = isTarget ? "当前换算" : "设为换算",
                Tag = game.Id,
                FontSize = 11,
                Padding = new Thickness(6, 4),
                Margin = new Thickness(3, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (isTarget)
                setTargetBtn.Classes.Add("accent");
            setTargetBtn.Click += (_, _) => SetTargetGame(game.Id);
            Grid.SetColumn(setTargetBtn, 1);
            actions.Children.Add(setTargetBtn);

            content.Children.Add(actions);

            var card = new Border
            {
                Tag = game.Id,
                Child = content,
                Background = cardBg,
                BorderBrush = (isTarget || isBase) ? accentStroke : defaultStroke,
                BorderThickness = (isTarget || isBase) ? new Thickness(1.5) : new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 10, 10),
            };
            _gameCardsGrid.Children.Add(card);
        }

        GameCardsHost.Children.Add(_gameCardsGrid);
        _gameCardsDirty = false;
    }

    private static IBrush ResolveBrush(string key)
    {
        try
        {
            if (Application.Current != null &&
                Application.Current.TryGetResource(key, null, out var value) &&
                value is IBrush brush)
                return brush;
        }
        catch { }
        return Brushes.Gray;
    }

    private void OnSetBaseGameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
            SetBaseGame(id);
    }

    private void SetTargetGame(string id)
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        var game = SensitivityConverter.FindById(id);
        p.TargetGameId = game.Id;
        double fov = ResolveFov(game, isBase: false);
        p.TargetFov = fov;
        WithBinding(() => TargetFovBox.Text = FormatNumber(fov));
        Recalculate();
        ScheduleSave();
        MarkGameCardsDirtyOrRender();
    }

    // ── 响应式布局 ───────────────────────────────────────────────────────────

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResultModesGrid.Columns = e.NewSize.Width < 760 ? 1 : 3;
        if (WorkspaceGrid.ColumnDefinitions.Count < 2) return;
        if (e.NewSize.Width < 920)
        {
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(TargetCard, 1);
            Grid.SetColumn(TargetCard, 0);
            TargetCard.Margin = new Thickness(0, 14, 0, 0);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(380);
            Grid.SetRow(TargetCard, 0);
            Grid.SetColumn(TargetCard, 1);
            TargetCard.Margin = new Thickness(14, 0, 0, 0);
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private double ResolveFov(GameSensitivityProfile game, bool isBase)
    {
        if (game.FovLocked) return game.DefaultFov;
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        p.PerGameFovs ??= new(StringComparer.OrdinalIgnoreCase);
        if (p.PerGameFovs.TryGetValue(game.Id, out var saved) && saved > 0)
            return saved;

        double stored = isBase ? p.BaseFov : p.TargetFov;
        string storedId = isBase ? p.BaseGameId : p.TargetGameId;
        return stored > 0 && string.Equals(storedId, game.Id, StringComparison.OrdinalIgnoreCase)
            ? stored
            : game.DefaultFov;
    }

    private void RememberFov(string gameId, double value)
    {
        App.Settings.Current.SensitivityPrefs ??= new();
        var p = App.Settings.Current.SensitivityPrefs;
        p.PerGameFovs ??= new(StringComparer.OrdinalIgnoreCase);
        p.PerGameFovs[gameId] = value;
    }

    private void ScheduleSave()
    {
        if (_binding) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        App.Settings.Save();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            App.Settings.Save();
        }
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
