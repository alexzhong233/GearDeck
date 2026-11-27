using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using GearDeck.Metrics;

namespace GearDeck.Widget;

internal static class Program
{
    internal static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck");

    internal static readonly string LogPath = Path.Combine(BaseDir, "logs", "widget.log");

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var renderArg = args.FirstOrDefault(a => a.StartsWith("--render-test=", StringComparison.OrdinalIgnoreCase));
        if (renderArg != null)
        {
            string outPath = renderArg["--render-test=".Length..].Trim('"');
            bool dark = args.Any(a => a.Equals("--dark", StringComparison.OrdinalIgnoreCase));
            var iconArg = args.FirstOrDefault(a => a.StartsWith("--icon=", StringComparison.OrdinalIgnoreCase));
            string? customIcon = iconArg is { Length: > 7 } ? iconArg["--icon=".Length..].Trim('"') : null;
            RunRenderTest(outPath, dark, customIcon);
            return;
        }

        var windowArg = args.FirstOrDefault(a => a.StartsWith("--window-test=", StringComparison.OrdinalIgnoreCase));
        if (windowArg != null)
        {
            string outPath = windowArg["--window-test=".Length..].Trim('"');
            RunWindowTest(outPath);
            return;
        }

        bool isBg = args.Any(a => string.Equals(a, "--widget-bg", StringComparison.OrdinalIgnoreCase));
        if (!isBg)
        {
            // 用户直接双击 Widget.exe 时，唤醒已运行的 GearDeck 主面板或拉起主程序，避免无界面退出或报错
            if (TryWakeMainApp())
                return;

            string appExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "GearDeck.App.exe"));
            if (File.Exists(appExe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(appExe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(appExe)!,
                    });
                }
                catch (Exception ex)
                {
                    Log("launch main app failed: " + ex.Message);
                }
                return;
            }
        }

        using var singleInstance = new Mutex(true, @"Local\GearDeck.Widget.SingleInstance", out bool created);
        if (!created)
        {
            if (!isBg)
                TryWakeMainApp();
            Log("duplicate instance skipped");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} start\n");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log("UI: " + e.Exception);
            ApplicationConfiguration.Initialize();
            Application.Run(new TaskbarForm());
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
        }
    }

    private static bool TryWakeMainApp()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\GearDeck.Activate", out var ev))
            {
                using (ev)
                {
                    Directory.CreateDirectory(BaseDir);
                    string path = Path.Combine(BaseDir, "widget-command.json");
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new
                    {
                        GroupId = "__activate__",
                        Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        X = 0,
                        Y = 0,
                        W = 0,
                        H = 0,
                    }));
                    File.Move(tmp, path, overwrite: true);
                    ev.Set();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static WidgetState CreateSampleState() => new()
    {
        Embedded = true,
        Height = 40,
        FontSize = 13,
        BlockWidth = 84,
        Monitor = true,
        MonitorTop = "CPU",
        MonitorBottom = "RAM",
        Groups =
        [
            new WidgetChip { GroupId = "builtin:mouse", Glyph = "\uE962", Text = "85% \u26A1", Tip = "鼠标", Low = false, Dim = false },
            new WidgetChip { GroupId = "builtin:keyboard", Glyph = "\uE765", Text = "--", Tip = "键盘", Low = false, Dim = true },
            new WidgetChip { GroupId = "builtin:headset", Glyph = "\uE7F5", Text = "L88%/R82%", TextTop = "L 88%", TextBottom = "R 82%\u26A1", Tip = "Sony INZONE Buds", Low = false, Dim = false },
        ],
    };

    private static void RunRenderTest(string outPath, bool dark, string? customIcon = null)
    {
        var state = CreateSampleState();
        if (!string.IsNullOrWhiteSpace(customIcon) && state.Groups.Count > 0)
            state.Groups[0].IconPath = customIcon;
        const double scale = 1.5;
        var sample = new SystemMetricsSnapshot
        {
            CpuPercent = 14,
            CpuFreqGhz = 4.65,
            RamPercent = 48,
            MemUsedGb = 15.4,
            MemTotalGb = 32.0,
            GpuPercent = 22,
            GpuTempC = 51,
            NetDownBytesPerSec = 1_500_000,
            NetUpBytesPerSec = 120_000,
            DiskActivePercent = 3,
            DiskSpaceUsedPercent = 62,
            DiskFreeGb = 380,
            Uptime = TimeSpan.FromHours(5.5),
        };
        var layout = TaskbarRenderer.BuildLayout(state, scale, maxHeightPx: 72, sample);
        using var bmp = TaskbarRenderer.RenderBitmap(layout, light: !dark, embedded: true, scale: scale, hoverIndex: -1, sample);
        var dualBlock = layout.Blocks.FirstOrDefault(b => !string.IsNullOrEmpty(b.TextTop) && !string.IsNullOrEmpty(b.TextBottom));
        if (dualBlock != null)
        {
            int topInk = 0;
            int botInk = 0;
            int midY = bmp.Height / 2;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = dualBlock.Bounds.Left; x < Math.Min(bmp.Width, dualBlock.Bounds.Right); x++)
                {
                    if (bmp.GetPixel(x, y).A > 32)
                    {
                        if (y < midY) topInk++;
                        else botInk++;
                    }
                }
            }
            if (topInk == 0 || botInk == 0)
                throw new InvalidOperationException($"Dual-line stacked chip render failed: topInk={topInk}, botInk={botInk}");
        }
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outPath, ImageFormat.Png);
    }

    private static void RunWindowTest(string outPath)
    {
        ApplicationConfiguration.Initialize();
        using var mockTray = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(100, 100, 900, 72),
            BackColor = Color.FromArgb(218, 218, 218),
        };
        mockTray.Show();

        using var widget = new TaskbarForm(testMode: true);
        widget.Show();
        Application.DoEvents();
        if (!widget.AttachTestParent(mockTray.Handle, CreateSampleState()))
        {
            Environment.Exit(2);
            return;
        }

        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();
        using var bmp = new Bitmap(mockTray.Width, mockTray.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(mockTray.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outPath, ImageFormat.Png);
        widget.Close();
        mockTray.Close();
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
}

internal sealed class WidgetState
{
    public string MainExe { get; set; } = "";
    public bool Embedded { get; set; } = true;
    public bool FlyoutOnHover { get; set; }
    public string Align { get; set; } = "right";
    public int Offset { get; set; }
    public int FloatX { get; set; } = -1;
    public int FloatY { get; set; } = -1;
    public int Height { get; set; } = 40;
    public int FontSize { get; set; } = 13;
    public int BlockWidth { get; set; } = 92;
    public bool Monitor { get; set; }
    public string MonitorTop { get; set; } = "CPU";
    public string MonitorBottom { get; set; } = "RAM";
    public List<WidgetChip> Groups { get; set; } = [];
    public long Seq { get; set; }
}

internal sealed class WidgetChip
{
    public string GroupId { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string Text { get; set; } = "";
    public string TextTop { get; set; } = "";
    public string TextBottom { get; set; } = "";
    public string Tip { get; set; } = "";
    public bool Low { get; set; }
    public bool Dim { get; set; }
}

internal enum BlockKind { Group, Monitor }

internal sealed class Block
{
    public Block(BlockKind kind, string groupId, string glyph, string iconPath, string text, string tip, bool low, bool dim, Rectangle bounds, string metricTop, string metricBottom, string textTop = "", string textBottom = "")
    {
        Kind = kind;
        GroupId = groupId;
        Glyph = glyph;
        IconPath = iconPath;
        Text = text;
        TextTop = textTop;
        TextBottom = textBottom;
        Tip = tip;
        Low = low;
        Dim = dim;
        Bounds = bounds;
        MetricTop = metricTop;
        MetricBottom = metricBottom;
    }

    public BlockKind Kind { get; }
    public string GroupId { get; }
    public string Glyph { get; }
    public string IconPath { get; }
    public string Text { get; }
    public string TextTop { get; }
    public string TextBottom { get; }
    public string Tip { get; }
    public bool Low { get; }
    public bool Dim { get; }
    public Rectangle Bounds { get; }
    public string MetricTop { get; }
    public string MetricBottom { get; }
}

internal sealed class WidgetLayout
{
    public required List<Block> Blocks { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FontSize { get; init; }
}

internal static class TaskbarRenderer
{
    private static readonly string IconFontFamily;
    private static readonly string TextFontFamily;
    private static readonly FontStyle TextFontStyle;
    private static readonly object IconCacheLock = new();
    private static readonly Dictionary<string, (DateTime WriteUtc, Bitmap Bmp)> IconCache = new(StringComparer.OrdinalIgnoreCase);

    static TaskbarRenderer()
    {
        using var fonts = new InstalledFontCollection();
        var names = new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        IconFontFamily = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        if (names.Contains("Segoe UI Semibold"))
        {
            TextFontFamily = "Segoe UI Semibold";
            TextFontStyle = FontStyle.Regular;
        }
        else
        {
            TextFontFamily = "Segoe UI";
            TextFontStyle = FontStyle.Bold;
        }
    }

    private static Bitmap? TryGetCustomIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        try
        {
            if (!File.Exists(iconPath)) return null;
            DateTime writeUtc = File.GetLastWriteTimeUtc(iconPath);
            lock (IconCacheLock)
            {
                if (IconCache.TryGetValue(iconPath, out var cached))
                {
                    if (cached.WriteUtc == writeUtc) return cached.Bmp;
                    cached.Bmp.Dispose();
                    IconCache.Remove(iconPath);
                }

                Bitmap loaded;
                if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using var fs = File.OpenRead(iconPath);
                    using var ico = new Icon(fs, new Size(64, 64));
                    loaded = ico.ToBitmap();
                }
                else
                {
                    using var fs = File.OpenRead(iconPath);
                    using var tmp = new Bitmap(fs);
                    loaded = new Bitmap(tmp);
                }
                IconCache[iconPath] = (writeUtc, loaded);
                return loaded;
            }
        }
        catch
        {
            return null;
        }
    }

    private static StringFormat CreateStringFormat()
    {
        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        return sf;
    }

    private static (Font IconFont, Font TextFont, Font SmallFont, Font TwoLineFont) CreateFonts(int fontSizeDip, double scale)
    {
        int clamped = Math.Clamp(fontSizeDip, 9, 20);
        float fontPx = clamped * (float)scale;
        var iconFont = new Font(IconFontFamily, fontPx * 1.12f, FontStyle.Regular, GraphicsUnit.Pixel);
        var textFont = new Font(TextFontFamily, fontPx, TextFontStyle, GraphicsUnit.Pixel);
        var smallFont = new Font(TextFontFamily, Math.Max(9f * (float)scale, fontPx * 0.82f), TextFontStyle, GraphicsUnit.Pixel);
        var twoLineFont = new Font(TextFontFamily, Math.Max(7.5f * (float)scale, fontPx * 0.74f), TextFontStyle, GraphicsUnit.Pixel);
        return (iconFont, textFont, smallFont, twoLineFont);
    }

    private static readonly SystemMetricsSnapshot RefSnapshot = new()
    {
        CpuPercent = 99,
        CpuFreqGhz = 5.99,
        RamPercent = 99,
        MemUsedGb = 99.9,
        MemTotalGb = 99.9,
        GpuPercent = 99,
        GpuTempC = 99,
        VramUsedGb = 24.0,
        VramTotalGb = 24.0,
        NetDownBytesPerSec = 99.9 * 1024 * 1024,
        NetUpBytesPerSec = 99.9 * 1024 * 1024,
        DiskActivePercent = 99,
        SystemDriveLabel = "C:",
        DiskSpaceUsedPercent = 99,
        Uptime = TimeSpan.FromHours(99.5),
    };

    public static WidgetLayout BuildLayout(WidgetState state, double scale, int maxHeightPx, SystemMetricsSnapshot metrics)
    {
        var blocks = new List<Block>();
        int desiredH = (int)Math.Round(Math.Clamp(state.Height, 24, 60) * scale);
        int blockH = maxHeightPx > 0
            ? Math.Clamp(desiredH, Math.Min(maxHeightPx, (int)Math.Round(24 * scale)), maxHeightPx)
            : desiredH;

        int edgePad = (int)Math.Round(6 * scale);
        int innerPadX = (int)Math.Round(8 * scale);
        int gap = (int)Math.Round(4 * scale);
        int blockGap = (int)Math.Round(2 * scale);
        int minBlockW = (int)Math.Round(Math.Clamp(state.BlockWidth, 48, 220) * scale);

        var (iconFont, textFont, smallFont, twoLineFont) = CreateFonts(state.FontSize, scale);
        using var _1 = iconFont;
        using var _2 = textFont;
        using var _3 = smallFont;
        using var _4 = twoLineFont;
        using var probeBmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(probeBmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var sf = CreateStringFormat();

        int iconPx = Math.Max(14, (int)Math.Round(Math.Clamp(state.FontSize, 9, 20) * scale * 1.25));
        int x = edgePad;
        foreach (var grp in state.Groups)
        {
            float iconW = TryGetCustomIcon(grp.IconPath) != null
                ? iconPx
                : g.MeasureString(grp.Glyph, iconFont, PointF.Empty, sf).Width;
            bool twoLine = !string.IsNullOrEmpty(grp.TextTop) && !string.IsNullOrEmpty(grp.TextBottom);
            float textW = twoLine
                ? Math.Max(
                    g.MeasureString(grp.TextTop, twoLineFont, PointF.Empty, sf).Width,
                    g.MeasureString(grp.TextBottom, twoLineFont, PointF.Empty, sf).Width)
                : g.MeasureString(grp.Text, textFont, PointF.Empty, sf).Width;
            int contentW = (int)Math.Ceiling(iconW + gap + textW);
            int w = Math.Max(minBlockW, contentW + innerPadX * 2);
            blocks.Add(new Block(BlockKind.Group, grp.GroupId, grp.Glyph, grp.IconPath ?? "", grp.Text, grp.Tip, grp.Low, grp.Dim,
                new Rectangle(x, 0, w, blockH), "", "", grp.TextTop ?? "", grp.TextBottom ?? ""));
            x += w + blockGap;
        }

        if (state.Monitor)
        {
            string top = SystemMetricsEngine.FormatTaskbar(state.MonitorTop, metrics);
            string bottom = SystemMetricsEngine.FormatTaskbar(state.MonitorBottom, metrics);
            string refTop = SystemMetricsEngine.FormatTaskbar(state.MonitorTop, RefSnapshot);
            string refBot = SystemMetricsEngine.FormatTaskbar(state.MonitorBottom, RefSnapshot);
            float maxW = Math.Max(
                Math.Max(g.MeasureString(top, smallFont, PointF.Empty, sf).Width,
                         g.MeasureString(refTop, smallFont, PointF.Empty, sf).Width),
                Math.Max(g.MeasureString(bottom, smallFont, PointF.Empty, sf).Width,
                         g.MeasureString(refBot, smallFont, PointF.Empty, sf).Width));
            int mw = Math.Max((int)Math.Round(78 * scale), (int)Math.Ceiling(maxW) + innerPadX * 2);
            blocks.Add(new Block(BlockKind.Monitor, "__monitor__", "\uE950", "", "", "", false, false,
                new Rectangle(x, 0, mw, blockH), state.MonitorTop, state.MonitorBottom));
            x += mw;
        }
        else if (blocks.Count > 0)
        {
            x -= blockGap;
        }

        int width = Math.Max((int)Math.Round(80 * scale), x + edgePad);
        return new WidgetLayout
        {
            Blocks = blocks,
            Width = width,
            Height = blockH,
            FontSize = Math.Clamp(state.FontSize, 9, 20),
        };
    }

    public static Bitmap RenderBitmap(WidgetLayout layout, bool light, bool embedded, double scale, int hoverIndex, SystemMetricsSnapshot metrics)
    {
        var bmp = new Bitmap(Math.Max(1, layout.Width), Math.Max(1, layout.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0, 0, 0, 0));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (embedded)
        {
            // Alpha=1 keeps the background visually transparent over the taskbar while guaranteeing mouse hit-testing across the whole strip.
            using var hitFill = new SolidBrush(Color.FromArgb(1, 128, 128, 128));
            g.FillRectangle(hitFill, 0, 0, bmp.Width, bmp.Height);
        }
        else
        {
            var pillRect = new RectangleF(0.5f, 0.5f, bmp.Width - 1f, bmp.Height - 1f);
            float pillRadius = (float)(8 * scale);
            using var bgBrush = new SolidBrush(light ? Color.FromArgb(234, 246, 246, 246) : Color.FromArgb(234, 32, 32, 32));
            using var borderPen = new Pen(light ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(55, 255, 255, 255), 1f);
            FillRounded(g, bgBrush, borderPen, pillRect, pillRadius);
        }

        var (iconFont, textFont, smallFont, twoLineFont) = CreateFonts(layout.FontSize, scale);
        using var _1 = iconFont;
        using var _2 = textFont;
        using var _3 = smallFont;
        using var _4 = twoLineFont;
        using var sf = CreateStringFormat();

        var primaryColor = light ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 246, 246, 246);
        var dimColor = light ? Color.FromArgb(205, 58, 58, 58) : Color.FromArgb(195, 205, 205, 205);
        var lowColor = light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 153, 164);

        using var hoverBrush = new SolidBrush(light ? Color.FromArgb(145, 255, 255, 255) : Color.FromArgb(32, 255, 255, 255));
        using var hoverPen = new Pen(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255), 1f);

        float gap = (float)(4 * scale);
        float innerPadX = (float)(8 * scale);
        float hoverMarginY = Math.Max(1f, (float)Math.Round(2 * scale));
        float hoverRadius = (float)(5 * scale);

        for (int i = 0; i < layout.Blocks.Count; i++)
        {
            var b = layout.Blocks[i];
            if (i == hoverIndex)
            {
                var hRect = new RectangleF(b.Bounds.X, hoverMarginY, b.Bounds.Width, Math.Max(8f, bmp.Height - hoverMarginY * 2f));
                FillRounded(g, hoverBrush, hoverPen, hRect, hoverRadius);
            }

            if (b.Kind == BlockKind.Monitor)
            {
                string top = SystemMetricsEngine.FormatTaskbar(b.MetricTop, metrics);
                string bottom = SystemMetricsEngine.FormatTaskbar(b.MetricBottom, metrics);
                using var fgBrush = new SolidBrush(primaryColor);
                float tx = b.Bounds.X + innerPadX;
                if (string.IsNullOrEmpty(bottom))
                {
                    var ts = g.MeasureString(top, smallFont, PointF.Empty, sf);
                    float yMid = Math.Max(0f, (bmp.Height - ts.Height) / 2f);
                    g.DrawString(top, smallFont, fgBrush, tx, yMid, sf);
                }
                else
                {
                    var ts1 = g.MeasureString(top, smallFont, PointF.Empty, sf);
                    var ts2 = g.MeasureString(bottom, smallFont, PointF.Empty, sf);
                    float lineGap = (float)(2 * scale);
                    if (ts1.Height + lineGap + ts2.Height > bmp.Height - 2f)
                        lineGap = Math.Max(0f, bmp.Height - 2f - ts1.Height - ts2.Height);
                    float totalH = ts1.Height + lineGap + ts2.Height;
                    float topY = Math.Max(0f, (bmp.Height - totalH) / 2f);
                    float botY = Math.Min(Math.Max(0f, bmp.Height - ts2.Height), topY + ts1.Height + lineGap);
                    g.DrawString(top, smallFont, fgBrush, tx, topY, sf);
                    g.DrawString(bottom, smallFont, fgBrush, tx, botY, sf);
                }
            }
            else
            {
                var iconColor = b.Dim ? dimColor : primaryColor;
                var textColor = b.Low ? lowColor : (b.Dim ? dimColor : primaryColor);
                bool hasTwoLines = !string.IsNullOrEmpty(b.TextTop) && !string.IsNullOrEmpty(b.TextBottom);
                SizeF tsTop = default, tsBot = default, tsSingle = default;
                float textW;
                if (hasTwoLines)
                {
                    tsTop = g.MeasureString(b.TextTop, twoLineFont, PointF.Empty, sf);
                    tsBot = g.MeasureString(b.TextBottom, twoLineFont, PointF.Empty, sf);
                    textW = Math.Max(tsTop.Width, tsBot.Width);
                }
                else
                {
                    tsSingle = g.MeasureString(b.Text, textFont, PointF.Empty, sf);
                    textW = tsSingle.Width;
                }
                using var textBrush = new SolidBrush(textColor);

                float textX;
                var customBmp = TryGetCustomIcon(b.IconPath);
                if (customBmp != null)
                {
                    float iconSize = Math.Max(14f, (float)Math.Round(layout.FontSize * scale * 1.25));
                    float totalW = iconSize + gap + textW;
                    float sx = b.Bounds.X + Math.Max(2f, (b.Bounds.Width - totalW) / 2f);
                    float iy = Math.Max(0f, (bmp.Height - iconSize) / 2f);

                    var prevInterp = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    lock (IconCacheLock)
                    {
                        if (b.Dim)
                        {
                            using var ia = new ImageAttributes();
                            ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.65f });
                            g.DrawImage(customBmp, new Rectangle((int)Math.Round(sx), (int)Math.Round(iy), (int)Math.Round(iconSize), (int)Math.Round(iconSize)),
                                0, 0, customBmp.Width, customBmp.Height, GraphicsUnit.Pixel, ia);
                        }
                        else
                        {
                            g.DrawImage(customBmp, sx, iy, iconSize, iconSize);
                        }
                    }
                    g.InterpolationMode = prevInterp;
                    textX = sx + iconSize + gap;
                }
                else
                {
                    var gs = g.MeasureString(b.Glyph, iconFont, PointF.Empty, sf);
                    float totalW = gs.Width + gap + textW;
                    float sx = b.Bounds.X + Math.Max(2f, (b.Bounds.Width - totalW) / 2f);
                    float iy = Math.Max(0f, (bmp.Height - gs.Height) / 2f);

                    using var iconBrush = new SolidBrush(iconColor);
                    g.DrawString(b.Glyph, iconFont, iconBrush, sx, iy, sf);
                    textX = sx + gs.Width + gap;
                }

                if (hasTwoLines)
                {
                    float lineGap = (float)(1.5 * scale);
                    if (tsTop.Height + lineGap + tsBot.Height > bmp.Height - 2f)
                        lineGap = Math.Max(0f, bmp.Height - 2f - tsTop.Height - tsBot.Height);
                    float totalH = tsTop.Height + lineGap + tsBot.Height;
                    float topY = Math.Max(0f, (bmp.Height - totalH) / 2f);
                    float botY = Math.Min(Math.Max(0f, bmp.Height - tsBot.Height), topY + tsTop.Height + lineGap);
                    g.DrawString(b.TextTop, twoLineFont, textBrush, textX, topY, sf);
                    g.DrawString(b.TextBottom, twoLineFont, textBrush, textX, botY, sf);
                }
                else
                {
                    float ty = Math.Max(0f, (bmp.Height - tsSingle.Height) / 2f);
                    g.DrawString(b.Text, textFont, textBrush, textX, ty, sf);
                }
            }
        }

        return bmp;
    }

    private static void FillRounded(Graphics g, Brush fill, Pen border, RectangleF rect, float radius)
    {
        float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (r <= 0.5f)
        {
            g.FillRectangle(fill, rect);
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }
        float d = r * 2f;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }
}

internal sealed class TaskbarForm : Form
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTopMost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint UlwAlpha = 0x00000002;
    private const uint GwHwndPrev = 3;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaCloaked = 14;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly string StatePath = Path.Combine(Program.BaseDir, "widget-state.json");

    private readonly ToolTip _tip = new() { InitialDelay = 250, ReshowDelay = 100, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _timer;
    private WidgetState _state = new();
    private WidgetLayout? _layout;
    private List<Block> _blocks = [];

    private IntPtr _attachedTray;
    private bool _usingChildMode;
    private bool _childLayeredFailed;
    private bool _floatingMode;
    private bool _loggedRenderOk;
    private bool _loggedNoTray;
    private bool _hiddenByFullscreen;

    private int _width = 180;
    private int _height = 40;
    private int _relX;
    private int _relY;
    private int _floatX = -1;
    private int _floatY = -1;
    private int _trayLeft;
    private int _trayTop;
    private int _lastTrayW;
    private int _lastTrayH;
    private int _lastLeftAnchor;
    private int _lastRightBoundary;
    private double _lastScale = 1.0;

    private int _appliedX = int.MinValue;
    private int _appliedY = int.MinValue;
    private int _appliedW;
    private int _appliedH;
    private bool _appliedTopMost;

    private DateTime _lastStateWriteUtc = DateTime.MinValue;
    private long _lastStateLength = -1;
    private long _lastStateSeq = -1;
    private long _lastStatsTick;
    private SystemMetricsSnapshot _metrics = new();
    private bool _lastLight = true;
    private int _hover = -1;
    private long _monitorHoverSince;
    private bool _monitorHoverSent;
    private bool _dirtyRender = true;

    private bool _dragging;
    private bool _moved;
    private POINT _dragCursorStart;
    private int _dragWinStartX;
    private int _dragWinStartY;
    private long _localPositionOverrideUntil;
    private readonly bool _testMode;
    private WinEventDelegate? _winEventProc;
    private IntPtr _hookFg;
    private long _lastZCheckTick;

    public TaskbarForm(bool testMode = false)
    {
        _testMode = testMode;
        _childLayeredFailed = !testMode;
        Text = "GearDeck Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        _metrics = SystemMetricsEngine.Sample();
        _lastStatsTick = Environment.TickCount64;
        _lastLight = IsLight();

        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) => TickLoop();
        if (!_testMode)
            _timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = (IntPtr)MaNoActivate;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_testMode) return;
        EnsureWinEventHooks();
        ReadStateIfChanged();
        EnsureModeAndPosition(forceRender: true);
    }

    private void EnsureWinEventHooks()
    {
        if (_winEventProc != null) return;
        _winEventProc = OnWinEvent;
        _hookFg = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _winEventProc, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
    }

    private static bool IsPopupFlyoutOpen()
        => FindWindow(null, "GearDeck Flyout") != IntPtr.Zero
           || FindWindow(null, "GearDeck Monitor") != IntPtr.Zero;

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!IsHandleCreated || IsDisposed || _testMode || !_state.Embedded || _usingChildMode || _hiddenByFullscreen)
            return;
        if (IsPopupFlyoutOpen())
            return;
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) tray = FindWindow("Shell_SecondaryTrayWnd", null);
        if (tray != IntPtr.Zero && !IsWindowAbove(Handle, tray))
        {
            uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | (IsWindowVisible(Handle) ? 0u : SwpShowWindow);
            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, flags);
        }
    }

    private void TickLoop()
    {
        if (ReadStateIfChanged())
            _dirtyRender = true;

        long now = Environment.TickCount64;
        if (_state.Monitor && now - _lastStatsTick >= 1000)
        {
            _metrics = SystemMetricsEngine.Sample();
            _lastStatsTick = now;
            _dirtyRender = true;
        }

        if (_state.FlyoutOnHover &&
            !_dragging &&
            _hover >= 0 && _hover < _blocks.Count &&
            !_monitorHoverSent &&
            now - _monitorHoverSince >= 180)
        {
            _monitorHoverSent = true;
            var hb = _blocks[_hover];
            var pt = new POINT { X = hb.Bounds.X, Y = hb.Bounds.Y };
            ClientToScreen(Handle, ref pt);
            string cmd = hb.Kind == BlockKind.Monitor
                ? "__monitor_hover__"
                : $"__group_hover__:{hb.GroupId}";
            WriteCommand(cmd, pt.X, pt.Y, hb.Bounds.Width, hb.Bounds.Height);
        }

        bool light = IsLight();
        if (light != _lastLight)
        {
            _lastLight = light;
            _dirtyRender = true;
        }

        EnsureModeAndPosition(forceRender: false);
    }

    private bool ReadStateIfChanged()
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var fi = new FileInfo(StatePath);
                    if (!fi.Exists) return false;
                    var writeUtc = fi.LastWriteTimeUtc;
                    long len = fi.Length;

                    if (writeUtc == _lastStateWriteUtc && len == _lastStateLength)
                        return false;

                    var json = File.ReadAllText(StatePath);
                    var parsed = JsonSerializer.Deserialize<WidgetState>(json);
                    if (parsed == null) return false;

                    if (parsed.Seq != 0 && parsed.Seq == _lastStateSeq &&
                        writeUtc == _lastStateWriteUtc && len == _lastStateLength)
                        return false;

                    _lastStateWriteUtc = writeUtc;
                    _lastStateLength = len;
                    _lastStateSeq = parsed.Seq;

                    if (Environment.TickCount64 < _localPositionOverrideUntil)
                    {
                        parsed.Align = _state.Align;
                        parsed.Offset = _state.Offset;
                        parsed.FloatX = _state.FloatX;
                        parsed.FloatY = _state.FloatY;
                    }
                    _state = parsed;
                    return true;
                }
                catch (IOException) when (attempt == 0)
                {
                    Thread.Sleep(25);
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureModeAndPosition(bool forceRender)
    {
        if (!IsHandleCreated) return;

        if (!_state.Embedded)
        {
            EnsureFloatingMode(forceRender);
            return;
        }

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) tray = FindWindow("Shell_SecondaryTrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            if (!_loggedNoTray)
            {
                _loggedNoTray = true;
                Program.Log("waiting for Shell_TrayWnd");
            }
            return;
        }
        _loggedNoTray = false;

        if (!GetWindowRect(tray, out var trayRect)) return;
        int trayW = trayRect.Right - trayRect.Left;
        int trayH = trayRect.Bottom - trayRect.Top;
        if (trayW <= 0 || trayH <= 0) return;

        if (!_testMode && !_childLayeredFailed &&
            FindWindowEx(tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero)
        {
            _childLayeredFailed = true;
            Program.Log("Win11 taskbar detected -> using TopMost layered mode");
        }

        if (!_childLayeredFailed)
        {
            if (!_usingChildMode || _attachedTray != tray || !IsWindow(_attachedTray))
                AttachToTray(tray);
        }
        if (_childLayeredFailed && (_usingChildMode || !_floatingMode))
        {
            DetachToTopMost();
            _floatingMode = true;
            _appliedX = int.MinValue;
            _dirtyRender = true;
        }

        uint dpi = GetDpiForWindow(tray);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        bool horizontal = trayW >= trayH;
        if (Math.Abs(scale - _lastScale) > 0.01) _dirtyRender = true;

        if (_dirtyRender || _layout == null || forceRender)
        {
            _layout = TaskbarRenderer.BuildLayout(_state, scale, horizontal ? trayH : trayW, _metrics);
            _blocks = _layout.Blocks;
            _width = _layout.Width;
            _height = _layout.Height;
        }

        int leftAnchor = (int)Math.Round(8 * scale);
        int rightBoundary = trayW - (int)Math.Round(160 * scale);
        if (horizontal)
        {
            if (IsWin11CenteredWithWidgets(tray))
                leftAnchor = (int)Math.Round(168 * scale);

            bool gotNotify = false;
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out var nr) && nr.Right > nr.Left && nr.Left > trayRect.Left + trayW / 2)
            {
                rightBoundary = nr.Left - trayRect.Left;
                gotNotify = true;
            }

            if (!gotNotify)
            {
                var rebar = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
                if (rebar != IntPtr.Zero && GetWindowRect(rebar, out var rr) && rr.Right > trayRect.Left + trayW / 2)
                    rightBoundary = rr.Right - trayRect.Left;
            }
        }

        _trayLeft = trayRect.Left;
        _trayTop = trayRect.Top;
        _lastTrayW = trayW;
        _lastTrayH = trayH;
        _lastLeftAnchor = leftAnchor;
        _lastRightBoundary = rightBoundary;
        _lastScale = scale;

        if (!_dragging)
        {
            if (Environment.TickCount64 < _localPositionOverrideUntil)
            {
                _relX = Math.Clamp(_relX, 0, Math.Max(0, trayW - _width));
                _relY = horizontal ? Math.Max(0, (trayH - _height) / 2) : Math.Clamp(_relY, 0, Math.Max(0, trayH - _height));
            }
            else if (horizontal)
            {
                _relX = WidgetLayoutRules.ComputeHorizontalRelX(_state.Align, _state.Offset, leftAnchor, rightBoundary, _width, trayW, scale);
                _relY = Math.Max(0, (trayH - _height) / 2);
            }
            else
            {
                _relX = Math.Max(0, (trayW - _width) / 2);
                _relY = WidgetLayoutRules.ComputeVerticalRelY(_state.Align, _state.Offset, _height, trayH, scale);
            }
        }

        if (_usingChildMode)
        {
            if (_appliedX != _relX || _appliedY != _relY || _appliedW != _width || _appliedH != _height || _appliedTopMost)
            {
                SetWindowPos(Handle, IntPtr.Zero, _relX, _relY, _width, _height, SwpNoActivate | SwpShowWindow);
                _appliedX = _relX; _appliedY = _relY; _appliedW = _width; _appliedH = _height; _appliedTopMost = false;
            }
            else if (GetWindow(Handle, GwHwndPrev) != IntPtr.Zero)
            {
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
        else
        {
            if (IsFullscreenForegroundOnMonitor(tray, trayRect))
            {
                if (!_hiddenByFullscreen)
                {
                    ShowWindow(Handle, SwHide);
                    _hiddenByFullscreen = true;
                }
                return;
            }

            int sx = _trayLeft + _relX;
            int sy = _trayTop + _relY;
            bool flyoutOpen = IsPopupFlyoutOpen();
            long now = Environment.TickCount64;
            bool behindTray = false;
            if (!flyoutOpen && now - _lastZCheckTick >= 800)
            {
                _lastZCheckTick = now;
                behindTray = !IsWindowAbove(Handle, tray);
            }
            if (_hiddenByFullscreen)
                _dirtyRender = true;
            if (!flyoutOpen && (_hiddenByFullscreen || behindTray || _appliedX != sx || _appliedY != sy || _appliedW != _width || _appliedH != _height || !_appliedTopMost))
            {
                uint swpFlags = SwpNoActivate | ((_hiddenByFullscreen || !IsWindowVisible(Handle)) ? SwpShowWindow : 0u);
                SetWindowPos(Handle, HwndTopMost, sx, sy, _width, _height, swpFlags);
                _appliedX = sx; _appliedY = sy; _appliedW = _width; _appliedH = _height; _appliedTopMost = true;
                _hiddenByFullscreen = false;
            }
        }

        if (_dirtyRender || forceRender)
            RenderCurrent();
    }

    private void EnsureFloatingMode(bool forceRender)
    {
        if (_usingChildMode || !_floatingMode)
        {
            DetachToTopMost();
            _floatingMode = true;
            _appliedX = int.MinValue;
            _dirtyRender = true;
        }

        uint dpi = GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        if (Math.Abs(scale - _lastScale) > 0.01) _dirtyRender = true;
        _lastScale = scale;

        if (_dirtyRender || _layout == null || forceRender)
        {
            _layout = TaskbarRenderer.BuildLayout(_state, scale, maxHeightPx: 0, _metrics);
            _blocks = _layout.Blocks;
            _width = _layout.Width;
            _height = _layout.Height;
        }

        if (!_dragging)
        {
            if (Environment.TickCount64 >= _localPositionOverrideUntil)
            {
                if (_state.FloatX >= -10000 && _state.FloatX != -1 && _state.FloatY >= -10000 && _state.FloatY != -1)
                {
                    _floatX = _state.FloatX;
                    _floatY = _state.FloatY;
                }
                else
                {
                    var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
                    _floatX = wa.Right - _width - (int)Math.Round(24 * scale);
                    _floatY = wa.Bottom - _height - (int)Math.Round(24 * scale);
                }
            }
            var vs = SystemInformation.VirtualScreen;
            _floatX = Math.Clamp(_floatX, vs.Left, Math.Max(vs.Left, vs.Right - _width));
            _floatY = Math.Clamp(_floatY, vs.Top, Math.Max(vs.Top, vs.Bottom - _height));
        }

        if (_appliedX != _floatX || _appliedY != _floatY || _appliedW != _width || _appliedH != _height || !_appliedTopMost)
        {
            SetWindowPos(Handle, HwndTopMost, _floatX, _floatY, _width, _height, SwpNoActivate | SwpShowWindow);
            _appliedX = _floatX; _appliedY = _floatY; _appliedW = _width; _appliedH = _height; _appliedTopMost = true;
        }

        if (_dirtyRender || forceRender)
            RenderCurrent();
    }

    private void AttachToTray(IntPtr tray)
    {
        _floatingMode = false;
        SetParent(Handle, tray);
        if (GetParent(Handle) != tray)
        {
            Program.Log($"SetParent failed err={Marshal.GetLastWin32Error()}");
            _childLayeredFailed = true;
            _usingChildMode = false;
            _attachedTray = IntPtr.Zero;
            return;
        }
        int style = GetWindowLong(Handle, GwlStyle);
        style = (style & ~(WsPopup | WsCaption | WsThickFrame)) | WsChild | WsVisible | WsClipSiblings;
        SetWindowLong(Handle, GwlStyle, style);

        int ex = GetWindowLong(Handle, GwlExStyle);
        int targetEx = (ex & ~(WsExAppWindow | WsExTopMost)) | WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLong(Handle, GwlExStyle, targetEx & ~WsExLayered);
        SetWindowLong(Handle, GwlExStyle, targetEx);

        _attachedTray = tray;
        _usingChildMode = true;
        _appliedX = int.MinValue;
        _dirtyRender = true;
    }

    internal bool AttachTestParent(IntPtr parentTray, WidgetState sampleState)
    {
        _timer.Stop();
        _state = sampleState;
        _lastLight = true;
        _metrics = new SystemMetricsSnapshot
        {
            CpuPercent = 14,
            RamPercent = 48,
            MemUsedGb = 15.4,
            MemTotalGb = 32.0,
        };
        _lastScale = 1.5;

        AttachToTray(parentTray);
        _layout = TaskbarRenderer.BuildLayout(_state, _lastScale, maxHeightPx: 72, _metrics);
        _blocks = _layout.Blocks;
        _width = _layout.Width;
        _height = _layout.Height;
        _relX = 40;
        _relY = 6;
        SetWindowPos(Handle, IntPtr.Zero, _relX, _relY, _width, _height, SwpNoActivate | SwpShowWindow);

        using var bmp = TaskbarRenderer.RenderBitmap(_layout, _lastLight, _state.Embedded, _lastScale, -1, _metrics);
        return ApplyBitmapToWindow(bmp, allowFallback: false);
    }

    private void DetachToTopMost()
    {
        if (_usingChildMode)
            SetParent(Handle, IntPtr.Zero);
        int style = GetWindowLong(Handle, GwlStyle);
        style = (style & ~(WsChild | WsCaption | WsThickFrame)) | WsPopup | WsVisible;
        SetWindowLong(Handle, GwlStyle, style);

        int ex = GetWindowLong(Handle, GwlExStyle);
        int targetEx = (ex & ~WsExAppWindow) | WsExLayered | WsExTopMost | WsExToolWindow | WsExNoActivate;
        SetWindowLong(Handle, GwlExStyle, targetEx);

        _usingChildMode = false;
        _attachedTray = IntPtr.Zero;
    }

    private void RenderCurrent()
    {
        if (!IsHandleCreated) return;
        _layout ??= TaskbarRenderer.BuildLayout(_state, _lastScale, _state.Embedded && _lastTrayH > 0 ? (_lastTrayW >= _lastTrayH ? _lastTrayH : _lastTrayW) : 0, _metrics);
        _blocks = _layout.Blocks;
        _width = _layout.Width;
        _height = _layout.Height;

        using var bmp = TaskbarRenderer.RenderBitmap(_layout, _lastLight, _state.Embedded, _lastScale, _hover, _metrics);
        ApplyBitmapToWindow(bmp, allowFallback: true);
        _dirtyRender = false;
    }

    private bool ApplyBitmapToWindow(Bitmap bmp, bool allowFallback)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var ptSrc = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,
            };

            bool ok = UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref ptSrc, 0, ref blend, UlwAlpha);
            if (!ok)
            {
                POINT ptDst;
                if (_usingChildMode && _attachedTray != IntPtr.Zero)
                {
                    ptDst = new POINT { X = _relX, Y = _relY };
                    ClientToScreen(_attachedTray, ref ptDst);
                }
                else
                {
                    ptDst = new POINT
                    {
                        X = _state.Embedded ? _trayLeft + _relX : _floatX,
                        Y = _state.Embedded ? _trayTop + _relY : _floatY,
                    };
                }
                ok = UpdateLayeredWindowWithPos(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, UlwAlpha);
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Program.Log($"UpdateLayeredWindow failed err={err} child={_usingChildMode}");
                if (allowFallback && _usingChildMode && !_childLayeredFailed)
                {
                    _childLayeredFailed = true;
                    BeginInvoke(new Action(() => EnsureModeAndPosition(forceRender: true)));
                }
                return false;
            }

            if (!_loggedRenderOk)
            {
                _loggedRenderOk = true;
                int px = _state.Embedded ? _relX : _floatX;
                int py = _state.Embedded ? _relY : _floatY;
                Program.Log($"rendered ok: mode={(_usingChildMode ? "child" : "topmost")} size={bmp.Width}x{bmp.Height} scale={_lastScale:F2} pos=({px},{py}) blocks={_blocks.Count}");
            }
            return true;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _moved = false;
        GetCursorPos(out _dragCursorStart);
        _dragWinStartX = _state.Embedded ? _relX : _floatX;
        _dragWinStartY = _state.Embedded ? _relY : _floatY;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            GetCursorPos(out var cur);
            int dx = cur.X - _dragCursorStart.X;
            int dy = cur.Y - _dragCursorStart.Y;
            int threshold = Math.Max(4, (int)Math.Round(4 * _lastScale));
            bool vertical = _state.Embedded && _lastTrayH > _lastTrayW;
            if (!_moved && (Math.Abs(dx) >= threshold || ((!_state.Embedded || vertical) && Math.Abs(dy) >= threshold)))
                _moved = true;

            if (_moved)
            {
                if (_state.Embedded)
                {
                    if (!vertical)
                    {
                        _relX = Math.Clamp(_dragWinStartX + dx, 0, Math.Max(0, _lastTrayW - _width));
                    }
                    else
                    {
                        _relY = Math.Clamp(_dragWinStartY + dy, 0, Math.Max(0, _lastTrayH - _height));
                    }
                    if (_usingChildMode)
                    {
                        SetWindowPos(Handle, IntPtr.Zero, _relX, _relY, 0, 0, SwpNoActivate | SwpNoSize | SwpNoZOrder);
                        _appliedX = _relX; _appliedY = _relY;
                    }
                    else
                    {
                        int sx = _trayLeft + _relX;
                        int sy = _trayTop + _relY;
                        SetWindowPos(Handle, HwndTopMost, sx, sy, 0, 0, SwpNoActivate | SwpNoSize);
                        _appliedX = sx; _appliedY = sy;
                    }
                }
                else
                {
                    var vs = SystemInformation.VirtualScreen;
                    _floatX = Math.Clamp(_dragWinStartX + dx, vs.Left, Math.Max(vs.Left, vs.Right - _width));
                    _floatY = Math.Clamp(_dragWinStartY + dy, vs.Top, Math.Max(vs.Top, vs.Bottom - _height));
                    SetWindowPos(Handle, HwndTopMost, _floatX, _floatY, 0, 0, SwpNoActivate | SwpNoSize);
                    _appliedX = _floatX; _appliedY = _floatY;
                }
                return;
            }
        }

        int hit = _blocks.FindIndex(b => b.Bounds.Contains(e.Location));
        if (hit != _hover)
        {
            _hover = hit;
            _monitorHoverSince = Environment.TickCount64;
            _monitorHoverSent = false;
            if (_state.FlyoutOnHover || (hit >= 0 && _blocks[hit].Kind == BlockKind.Monitor))
                _tip.SetToolTip(this, "");
            else
                _tip.SetToolTip(this, hit >= 0 ? _blocks[hit].Tip : "");
            RenderCurrent();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _monitorHoverSent = false;
        if (_hover != -1)
        {
            _hover = -1;
            _tip.SetToolTip(this, "");
            RenderCurrent();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        _dragging = false;
        if (_moved)
        {
            _moved = false;
            SavePosition();
            return;
        }

        var hit = _blocks.FirstOrDefault(b => b.Bounds.Contains(e.Location));
        try
        {
            if (hit != null)
            {
                var pt = new POINT { X = hit.Bounds.X, Y = hit.Bounds.Y };
                ClientToScreen(Handle, ref pt);
                WriteCommand(hit.GroupId, pt.X, pt.Y, hit.Bounds.Width, hit.Bounds.Height);
            }

            bool mainRunning = false;
            if (Mutex.TryOpenExisting(@"Local\GearDeck.SingleInstance", out var existing))
            {
                mainRunning = true;
                existing.Dispose();
            }
            if (!mainRunning && !string.IsNullOrWhiteSpace(_state.MainExe) && File.Exists(_state.MainExe))
                Process.Start(new ProcessStartInfo(_state.MainExe) { UseShellExecute = true });
        }
        catch { }
    }

    private void SavePosition()
    {
        try
        {
            _localPositionOverrideUntil = Environment.TickCount64 + 2500;
            if (_state.Embedded)
            {
                if (_lastTrayW >= _lastTrayH)
                {
                    bool left = (_relX + _width / 2) < (_lastTrayW / 2);
                    _state.Align = left ? "left" : "right";
                    _state.Offset = WidgetLayoutRules.ComputeHorizontalOffsetDip(
                        _state.Align, _relX, _lastLeftAnchor, _lastRightBoundary, _width, _lastScale);
                }
                else
                {
                    bool top = (_relY + _height / 2) < (_lastTrayH / 2);
                    _state.Align = top ? "left" : "right";
                    _state.Offset = WidgetLayoutRules.ComputeVerticalOffsetDip(
                        _state.Align, _relY, _height, _lastTrayH, _lastScale);
                }
            }
            else
            {
                _state.FloatX = _floatX;
                _state.FloatY = _floatY;
            }

            var path = Path.Combine(Program.BaseDir, "widget-position.json");
            var tmpPath = path + ".tmp";
            Directory.CreateDirectory(Program.BaseDir);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(new
            {
                Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Align = _state.Align,
                Offset = _state.Offset,
                FloatX = _state.FloatX,
                FloatY = _state.FloatY,
            }));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch { }
    }

    private static void WriteCommand(string groupId, int x, int y, int w, int h)
    {
        try
        {
            var path = Path.Combine(Program.BaseDir, "widget-command.json");
            var tmpPath = path + ".tmp";
            Directory.CreateDirectory(Program.BaseDir);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(new
            {
                GroupId = groupId,
                Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                X = x,
                Y = y,
                W = w,
                H = h,
            }));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch { }
    }

    private static bool IsWin11CenteredWithWidgets(IntPtr tray)
    {
        try
        {
            if (FindWindowEx(tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) == IntPtr.Zero)
                return false;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
            bool centered = key?.GetValue("TaskbarAl") is not int al || al != 0;
            bool widgetsOn = key?.GetValue("TaskbarDa") is not int da || da != 0;
            return centered && widgetsOn;
        }
        catch { return false; }
    }

    private static bool IsWindowAbove(IntPtr candidateAbove, IntPtr referenceBelow)
    {
        IntPtr cur = GetWindow(referenceBelow, GwHwndPrev);
        for (int steps = 0; cur != IntPtr.Zero && steps < 256; steps++)
        {
            if (cur == candidateAbove) return true;
            cur = GetWindow(cur, GwHwndPrev);
        }
        return false;
    }

    private bool IsFullscreenForegroundOnMonitor(IntPtr tray, RECT trayRect)
    {
        try
        {
            IntPtr mon = MonitorFromWindow(tray, MonitorDefaultToNearest);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            if (WidgetLayoutRules.IsTrayAutoHidden(
                    trayRect.Left, trayRect.Top, trayRect.Right, trayRect.Bottom,
                    mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom))
                return true;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == tray || fg == Handle) return false;
            RECT fgRect;
            int dwmBounds = DwmGetWindowAttribute(fg, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out fgRect, Marshal.SizeOf<RECT>());
            if (dwmBounds != 0 && !GetWindowRect(fg, out fgRect)) return false;

            var sb = new StringBuilder(128);
            int len = GetClassName(fg, sb, sb.Capacity);
            string className = len > 0 ? sb.ToString(0, len) : "";

            GetWindowThreadProcessId(fg, out uint fgPid);
            GetWindowThreadProcessId(tray, out uint trayPid);
            bool sameProcessAsTray = fgPid != 0 && fgPid == trayPid;

            int style = GetWindowLong(fg, GwlStyle);
            int exStyle = GetWindowLong(fg, GwlExStyle);
            bool isVisible = IsWindowVisible(fg);
            bool isCloaked = DwmGetWindowAttribute(fg, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

            return WidgetLayoutRules.ShouldTreatAsFullscreen(
                className, sameProcessAsTray, style, exStyle, isVisible, isCloaked,
                fgRect.Left, fgRect.Top, fgRect.Right, fgRect.Bottom,
                mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom,
                isMaximized: IsZoomed(fg));
        }
        catch { return false; }
    }

    private static bool IsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return true; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hookFg != IntPtr.Zero) { UnhookWinEvent(_hookFg); _hookFg = IntPtr.Zero; }
        _winEventProc = null;
        _timer.Stop();
        _timer.Dispose();
        _tip.Dispose();
        base.OnFormClosed(e);
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    private static extern bool UpdateLayeredWindowWithPos(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}
