using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GearDeck.Metrics;
using SkiaSharp;

namespace GearDeck.Widget;

internal static class Program
{
    internal static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GearDeck");

    internal static readonly string LogPath = Path.Combine(BaseDir, "logs", "widget.log");

    private static volatile bool _debugLogEnabled;

    internal static void SetDebugLogEnabled(bool enabled) => _debugLogEnabled = enabled;

    internal static void Log(string message)
    {
        if (!_debugLogEnabled) return;
        WriteLogLine(message);
    }

    internal static void LogFatal(string message) => WriteLogLine(message);

    private static void WriteLogLine(string message)
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
        try { SetProcessDpiAwarenessContext(new IntPtr(-4) /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */); }
        catch { }

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
        if (args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase)))
            SetDebugLogEnabled(true);
        if (!isBg)
        {
            if (TryWakeMainApp())
                return;

            string? mainExe = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "GearDeck.Desktop.exe"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "GearDeck.Desktop.exe")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "GearDeck.App.exe")),
            }.FirstOrDefault(File.Exists);

            if (mainExe != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(mainExe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(mainExe)!,
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
            using var widget = new TaskbarWindow();
            widget.RunMessageLoop();
        }
        catch (Exception ex)
        {
            LogFatal("FATAL: " + ex);
        }
    }

    private static bool TryWakeMainApp()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\GearDeck.Desktop.Activate", out var ev))
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

    private static void SavePng(SKBitmap bmp, string outPath)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Failed to encode PNG");
        using var fs = File.Create(outPath);
        data.SaveTo(fs);
    }

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
                    if (bmp.GetPixel(x, y).Alpha > 32)
                    {
                        if (y < midY) topInk++;
                        else botInk++;
                    }
                }
            }
            if (topInk == 0 || botInk == 0)
                throw new InvalidOperationException($"Dual-line stacked chip render failed: topInk={topInk}, botInk={botInk}");
        }
        SavePng(bmp, outPath);
    }

    private static void RunWindowTest(string outPath)
    {
        string mockClass = $"GearDeckMockTray_{Environment.TickCount64}";
        IntPtr bgBrush = CreateSolidBrush(0x00DADADA); // RGB(218, 218, 218)
        Win32WndProc mockProc = DefWindowProcW;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(mockProc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = bgBrush,
            lpszClassName = mockClass,
        };
        RegisterClassExW(ref wc);

        IntPtr mockHwnd = CreateWindowExW(
            0x00000080 /* WS_EX_TOOLWINDOW */,
            mockClass,
            "MockTray",
            unchecked((int)0x80000000) | 0x10000000 /* WS_POPUP | WS_VISIBLE */,
            100, 100, 900, 72,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (mockHwnd == IntPtr.Zero)
        {
            DeleteObject(bgBrush);
            Environment.Exit(2);
            return;
        }

        try
        {
            using var widget = new TaskbarWindow(testMode: true);
            PumpEvents();
            if (!widget.AttachTestParent(mockHwnd, CreateSampleState()))
            {
                Environment.Exit(2);
                return;
            }

            PumpEvents();
            Thread.Sleep(80);
            PumpEvents();

            const int w = 900, h = 72;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            IntPtr hDib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
            IntPtr oldBmp = SelectObject(memDc, hDib);
            try
            {
                PrintWindow(mockHwnd, memDc, 2);
                byte[] pixels = new byte[w * h * 4];
                Marshal.Copy(bitsPtr, pixels, 0, pixels.Length);
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] == 0) pixels[i] = 255;

                using var skBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                Marshal.Copy(pixels, 0, skBmp.GetPixels(), pixels.Length);
                SavePng(skBmp, outPath);
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally
        {
            DestroyWindow(mockHwnd);
            UnregisterClassW(mockClass, wc.hInstance);
            DeleteObject(bgBrush);
            GC.KeepAlive(mockProc);
        }
    }

    private static void PumpEvents()
    {
        while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    internal delegate IntPtr Win32WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint removeMsg);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
}

internal readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
}

internal sealed class WidgetState
{
    public string MainExe { get; set; } = "";
    public bool Debug { get; set; }
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
    public Block(BlockKind kind, string groupId, string glyph, string iconPath, string text, string tip, bool low, bool dim, IntRect bounds, string metricTop, string metricBottom, string textTop = "", string textBottom = "")
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
    public IntRect Bounds { get; }
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
    private static readonly SKTypeface IconTypeface;
    private static readonly SKTypeface TextTypeface;
    private static readonly object IconCacheLock = new();
    private static readonly Dictionary<string, (DateTime WriteUtc, SKBitmap Bmp)> IconCache = new(StringComparer.OrdinalIgnoreCase);

    static TaskbarRenderer()
    {
        var families = new HashSet<string>(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase);
        string iconFamily = families.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        IconTypeface = SKTypeface.FromFamilyName(iconFamily, SKFontStyle.Normal) ?? SKTypeface.Default;

        TextTypeface = families.Contains("Segoe UI Semibold")
            ? (SKTypeface.FromFamilyName("Segoe UI Semibold", SKFontStyle.Normal) ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default)
            : (SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default);
    }

    private static SKBitmap? TryGetCustomIcon(string? iconPath)
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

                SKBitmap? loaded = iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                    ? DecodeIcoFile(iconPath)
                    : SKBitmap.Decode(iconPath);

                if (loaded == null) return null;
                IconCache[iconPath] = (writeUtc, loaded);
                return loaded;
            }
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? DecodeIcoFile(string icoPath)
    {
        IntPtr hIcon = LoadImageW(IntPtr.Zero, icoPath, 1 /* IMAGE_ICON */, 64, 64, 0x0010 /* LR_LOADFROMFILE */);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;
            try
            {
                IntPtr hBmp = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                if (hBmp == IntPtr.Zero) return null;
                var bm = new BITMAP();
                if (GetObjectW(hBmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
                    return null;
                int w = bm.bmWidth;
                int h = ii.hbmColor != IntPtr.Zero ? bm.bmHeight : Math.Max(1, bm.bmHeight / 2);
                var bmi = new Program.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Program.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };
                byte[] pixels = new byte[w * h * 4];
                IntPtr hdc = Program.GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(hdc, hBmp, 0, (uint)h, pixels, ref bmi, 0) == 0)
                        return null;
                }
                finally { Program.ReleaseDC(IntPtr.Zero, hdc); }

                bool anyAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha)
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

                var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                Marshal.Copy(pixels, 0, sk.GetPixels(), pixels.Length);
                return sk;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) Program.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) Program.DeleteObject(ii.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static SKPaint CreatePaint(SKTypeface typeface, float textSizePx, SKColor color = default) => new()
    {
        Typeface = typeface,
        TextSize = textSizePx,
        Color = color,
        IsAntialias = true,
        SubpixelText = true,
        LcdRenderText = false,
    };

    [ThreadStatic]
    private static (int FontSize, int ScaleKey, SKPaint Icon, SKPaint Text, SKPaint Small, SKPaint TwoLine)? _paintCache;

    private static (SKPaint Icon, SKPaint Text, SKPaint Small, SKPaint TwoLine) GetCachedPaints(int fontSizeDip, double scale)
    {
        int clamped = Math.Clamp(fontSizeDip, 9, 20);
        int scaleKey = (int)Math.Round(scale * 100);
        if (_paintCache is { } cached && cached.FontSize == clamped && cached.ScaleKey == scaleKey)
            return (cached.Icon, cached.Text, cached.Small, cached.TwoLine);

        if (_paintCache is { } old)
        {
            old.Icon.Dispose();
            old.Text.Dispose();
            old.Small.Dispose();
            old.TwoLine.Dispose();
        }

        float fontPx = clamped * (float)scale;
        var next = (
            clamped,
            scaleKey,
            CreatePaint(IconTypeface, fontPx * 1.12f),
            CreatePaint(TextTypeface, fontPx),
            CreatePaint(TextTypeface, Math.Max(9f * (float)scale, fontPx * 0.82f)),
            CreatePaint(TextTypeface, Math.Max(7.5f * (float)scale, fontPx * 0.74f))
        );
        _paintCache = next;
        return (next.Item3, next.Item4, next.Item5, next.Item6);
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

        var (iconPaint, textPaint, smallPaint, twoLinePaint) = GetCachedPaints(state.FontSize, scale);

        int iconPx = Math.Max(14, (int)Math.Round(Math.Clamp(state.FontSize, 9, 20) * scale * 1.25));
        int x = edgePad;
        foreach (var grp in state.Groups)
        {
            float iconW = TryGetCustomIcon(grp.IconPath) != null
                ? iconPx
                : iconPaint.MeasureText(grp.Glyph ?? "");
            bool twoLine = !string.IsNullOrEmpty(grp.TextTop) && !string.IsNullOrEmpty(grp.TextBottom);
            float textW = twoLine
                ? Math.Max(twoLinePaint.MeasureText(grp.TextTop), twoLinePaint.MeasureText(grp.TextBottom))
                : textPaint.MeasureText(grp.Text ?? "");
            int contentW = (int)Math.Ceiling(iconW + gap + textW);
            int w = Math.Max(minBlockW, contentW + innerPadX * 2);
            blocks.Add(new Block(BlockKind.Group, grp.GroupId, grp.Glyph ?? "", grp.IconPath ?? "", grp.Text ?? "", grp.Tip ?? "", grp.Low, grp.Dim,
                new IntRect(x, 0, w, blockH), "", "", grp.TextTop ?? "", grp.TextBottom ?? ""));
            x += w + blockGap;
        }

        if (state.Monitor)
        {
            string top = SystemMetricsEngine.FormatTaskbar(state.MonitorTop, metrics);
            string bottom = SystemMetricsEngine.FormatTaskbar(state.MonitorBottom, metrics);
            string refTop = SystemMetricsEngine.FormatTaskbar(state.MonitorTop, RefSnapshot);
            string refBot = SystemMetricsEngine.FormatTaskbar(state.MonitorBottom, RefSnapshot);
            float maxW = Math.Max(
                Math.Max(smallPaint.MeasureText(top), smallPaint.MeasureText(refTop)),
                Math.Max(smallPaint.MeasureText(bottom), smallPaint.MeasureText(refBot)));
            int mw = Math.Max((int)Math.Round(78 * scale), (int)Math.Ceiling(maxW) + innerPadX * 2);
            blocks.Add(new Block(BlockKind.Monitor, "__monitor__", "\uE950", "", "", "", false, false,
                new IntRect(x, 0, mw, blockH), state.MonitorTop, state.MonitorBottom));
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

    public static SKBitmap RenderBitmap(WidgetLayout layout, bool light, bool embedded, double scale, int hoverIndex, SystemMetricsSnapshot metrics)
    {
        var info = new SKImageInfo(Math.Max(1, layout.Width), Math.Max(1, layout.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        if (embedded)
        {
            // Alpha=1 keeps the background visually transparent over the taskbar while guaranteeing mouse hit-testing across the whole strip.
            using var hitPaint = new SKPaint { Color = new SKColor(128, 128, 128, 1), Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, bmp.Width, bmp.Height, hitPaint);
        }
        else
        {
            var pillRect = new SKRect(0.5f, 0.5f, bmp.Width - 0.5f, bmp.Height - 0.5f);
            float pillRadius = (float)(8 * scale);
            using var bgPaint = new SKPaint
            {
                Color = light ? new SKColor(246, 246, 246, 234) : new SKColor(32, 32, 32, 234),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            using var borderPaint = new SKPaint
            {
                Color = light ? new SKColor(0, 0, 0, 45) : new SKColor(255, 255, 255, 55),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
            };
            canvas.DrawRoundRect(pillRect, pillRadius, pillRadius, bgPaint);
            canvas.DrawRoundRect(pillRect, pillRadius, pillRadius, borderPaint);
        }

        var (iconPaint, textPaint, smallPaint, twoLinePaint) = GetCachedPaints(layout.FontSize, scale);

        var primaryColor = light ? new SKColor(20, 20, 20, 255) : new SKColor(246, 246, 246, 255);
        var dimColor = light ? new SKColor(58, 58, 58, 205) : new SKColor(205, 205, 205, 195);
        var lowColor = light ? new SKColor(196, 43, 28, 255) : new SKColor(255, 153, 164, 255);

        using var hoverFill = new SKPaint
        {
            Color = light ? new SKColor(255, 255, 255, 145) : new SKColor(255, 255, 255, 32),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var hoverBorder = new SKPaint
        {
            Color = light ? new SKColor(0, 0, 0, 22) : new SKColor(255, 255, 255, 36),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        };

        float gap = (float)(4 * scale);
        float innerPadX = (float)(8 * scale);
        float hoverMarginY = Math.Max(1f, (float)Math.Round(2 * scale));
        float hoverRadius = (float)(5 * scale);

        for (int i = 0; i < layout.Blocks.Count; i++)
        {
            var b = layout.Blocks[i];
            if (i == hoverIndex)
            {
                float hh = Math.Max(8f, bmp.Height - hoverMarginY * 2f);
                var hRect = new SKRect(b.Bounds.X, hoverMarginY, b.Bounds.X + b.Bounds.Width, hoverMarginY + hh);
                canvas.DrawRoundRect(hRect, hoverRadius, hoverRadius, hoverFill);
                canvas.DrawRoundRect(hRect, hoverRadius, hoverRadius, hoverBorder);
            }

            if (b.Kind == BlockKind.Monitor)
            {
                string top = SystemMetricsEngine.FormatTaskbar(b.MetricTop, metrics);
                string bottom = SystemMetricsEngine.FormatTaskbar(b.MetricBottom, metrics);
                smallPaint.Color = primaryColor;
                float tx = b.Bounds.X + innerPadX;
                smallPaint.GetFontMetrics(out var sfm);
                float lineH = sfm.Descent - sfm.Ascent;

                if (string.IsNullOrEmpty(bottom))
                {
                    float baseline = Math.Max(0f, (bmp.Height - lineH) / 2f) - sfm.Ascent;
                    canvas.DrawText(top, tx, baseline, smallPaint);
                }
                else
                {
                    float lineGap = (float)(2 * scale);
                    if (lineH * 2f + lineGap > bmp.Height - 2f)
                        lineGap = Math.Max(0f, bmp.Height - 2f - lineH * 2f);
                    float totalH = lineH * 2f + lineGap;
                    float topY = Math.Max(0f, (bmp.Height - totalH) / 2f);
                    float botY = Math.Min(Math.Max(0f, bmp.Height - lineH), topY + lineH + lineGap);
                    canvas.DrawText(top, tx, topY - sfm.Ascent, smallPaint);
                    canvas.DrawText(bottom, tx, botY - sfm.Ascent, smallPaint);
                }
            }
            else
            {
                var iconColor = b.Dim ? dimColor : primaryColor;
                var textColor = b.Low ? lowColor : (b.Dim ? dimColor : primaryColor);
                bool hasTwoLines = !string.IsNullOrEmpty(b.TextTop) && !string.IsNullOrEmpty(b.TextBottom);

                float textW = hasTwoLines
                    ? Math.Max(twoLinePaint.MeasureText(b.TextTop), twoLinePaint.MeasureText(b.TextBottom))
                    : textPaint.MeasureText(b.Text);

                float textX;
                var customBmp = TryGetCustomIcon(b.IconPath);
                if (customBmp != null)
                {
                    float iconSize = Math.Max(14f, (float)Math.Round(layout.FontSize * scale * 1.25));
                    float totalW = iconSize + gap + textW;
                    float sx = b.Bounds.X + Math.Max(2f, (b.Bounds.Width - totalW) / 2f);
                    float iy = Math.Max(0f, (bmp.Height - iconSize) / 2f);

                    using var imgPaint = new SKPaint
                    {
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High,
                        Color = b.Dim ? new SKColor(255, 255, 255, 166) : SKColors.White,
                    };
                    lock (IconCacheLock)
                    {
                        var dst = new SKRect(sx, iy, sx + iconSize, iy + iconSize);
                        canvas.DrawBitmap(customBmp, dst, imgPaint);
                    }
                    textX = sx + iconSize + gap;
                }
                else
                {
                    float iconW = iconPaint.MeasureText(b.Glyph);
                    float totalW = iconW + gap + textW;
                    float sx = b.Bounds.X + Math.Max(2f, (b.Bounds.Width - totalW) / 2f);
                    iconPaint.GetFontMetrics(out var ifm);
                    float iconH = ifm.Descent - ifm.Ascent;
                    float iy = Math.Max(0f, (bmp.Height - iconH) / 2f) - ifm.Ascent;
                    iconPaint.Color = iconColor;
                    canvas.DrawText(b.Glyph, sx, iy, iconPaint);
                    textX = sx + iconW + gap;
                }

                if (hasTwoLines)
                {
                    twoLinePaint.Color = textColor;
                    twoLinePaint.GetFontMetrics(out var tfm);
                    float lineH = tfm.Descent - tfm.Ascent;
                    float lineGap = (float)(1.5 * scale);
                    if (lineH * 2f + lineGap > bmp.Height - 2f)
                        lineGap = Math.Max(0f, bmp.Height - 2f - lineH * 2f);
                    float totalH = lineH * 2f + lineGap;
                    float topY = Math.Max(0f, (bmp.Height - totalH) / 2f);
                    float botY = Math.Min(Math.Max(0f, bmp.Height - lineH), topY + lineH + lineGap);
                    canvas.DrawText(b.TextTop, textX, topY - tfm.Ascent, twoLinePaint);
                    canvas.DrawText(b.TextBottom, textX, botY - tfm.Ascent, twoLinePaint);
                }
                else
                {
                    textPaint.Color = textColor;
                    textPaint.GetFontMetrics(out var sfm);
                    float lineH = sfm.Descent - sfm.Ascent;
                    float ty = Math.Max(0f, (bmp.Height - lineH) / 2f) - sfm.Ascent;
                    canvas.DrawText(b.Text, textX, ty, textPaint);
                }
            }
        }

        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref Program.BITMAPINFOHEADER lpbi, uint uUsage);
}

internal sealed class TaskbarWindow : IDisposable
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
    private const int WmDestroy = 0x0002;
    private const int WmMouseActivate = 0x0021;
    private const int WmTimer = 0x0113;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmMouseLeave = 0x02A3;
    private const int WmUser = 0x0400;
    private const int WmFallbackReevaluate = WmUser + 42;
    private const int WmStateChanged = WmUser + 43;
    private const int WmForegroundChanged = WmUser + 44;
    private const uint TimerIdMain = 1;
    private const uint TimerIdHover = 2;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const uint TmeLeave = 0x00000002;
    private const int TtmAddToolW = WmUser + 50;
    private const int TtmUpdateTipTextW = WmUser + 57;
    private const uint TtfIdIsHwnd = 0x0001;
    private const uint TtfSubclass = 0x0010;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly string StatePath = Path.Combine(Program.BaseDir, "widget-state.json");

    private readonly string _className;
    private readonly Program.Win32WndProc _wndProc;
    private readonly IntPtr _hInstance;
    private IntPtr _hwnd;
    private IntPtr _tooltipHwnd;
    private string _currentTip = "";
    private bool _trackingLeave;
    private bool _disposed;

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
    private readonly long _startTick;
    private long _lastTrimTick;
    private bool _initialTrimDone;
    private long _cachedLightTick;
    private bool _cachedLightVal = true;
    private static long s_win11CenteredTick;
    private static bool s_win11CenteredVal;
    private static IntPtr s_win11CenteredTray;
    private IntPtr _dibScreenDc;
    private IntPtr _dibMemDc;
    private IntPtr _hDib;
    private IntPtr _oldDibBmp;
    private IntPtr _dibBitsPtr;
    private int _dibW;
    private int _dibH;
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
    private FileSystemWatcher? _stateWatcher;
    private int _stateNotifyPending;
    private int _fgNotifyPending;
    private uint _mainTimerMs;

    public IntPtr Handle => _hwnd;
    private bool IsHandleCreated => _hwnd != IntPtr.Zero && !_disposed;

    public TaskbarWindow(bool testMode = false)
    {
        _testMode = testMode;
        _childLayeredFailed = !testMode;
        _startTick = Environment.TickCount64;
        _lastTrimTick = _startTick;
        _lastLight = IsLightCached();

        _hInstance = Program.GetModuleHandleW(null);
        _className = $"GearDeckWidgetWnd_{Guid.NewGuid():N}";
        _wndProc = WndProc;

        var wc = new Program.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Program.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */),
            lpszClassName = _className,
        };
        Program.RegisterClassExW(ref wc);

        int exStyle = WsExLayered | WsExToolWindow | WsExNoActivate;
        _hwnd = Program.CreateWindowExW(
            exStyle,
            _className,
            "GearDeck Widget",
            WsPopup | (testMode ? WsVisible : 0),
            0, 0, _width, _height,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (!_testMode && _hwnd != IntPtr.Zero)
        {
            InitTooltip();
            EnsureWinEventHooks();
            StartStateWatcher();
            ReadStateIfChanged();
            if (_state.Monitor)
            {
                _metrics = SystemMetricsEngine.Sample(SystemMetricsEngine.FromMetricIds(_state.MonitorTop, _state.MonitorBottom));
                _lastStatsTick = Environment.TickCount64;
            }
            EnsureModeAndPosition(forceRender: true);
            UpdateMainTimerCadence();
        }
    }

    public void RunMessageLoop()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Program.TranslateMessage(ref msg);
            Program.DispatchMessageW(ref msg);
        }
    }

    private void InitTooltip()
    {
        _tooltipHwnd = Program.CreateWindowExW(
            WsExTopMost,
            "tooltips_class32",
            "",
            unchecked((int)0x80000000) | 0x01 /* TTS_ALWAYSTIP */,
            0, 0, 0, 0,
            _hwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_tooltipHwnd == IntPtr.Zero) return;

        var ti = new TOOLINFOW
        {
            cbSize = Marshal.SizeOf<TOOLINFOW>(),
            uFlags = TtfIdIsHwnd | TtfSubclass,
            hwnd = _hwnd,
            uId = _hwnd,
            lpszText = IntPtr.Zero,
        };
        IntPtr mem = Marshal.AllocHGlobal(ti.cbSize);
        try
        {
            Marshal.StructureToPtr(ti, mem, false);
            SendMessageW(_tooltipHwnd, TtmAddToolW, IntPtr.Zero, mem);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private void SetTipText(string text)
    {
        text ??= "";
        if (string.Equals(_currentTip, text, StringComparison.Ordinal))
            return;
        _currentTip = text;
        if (_tooltipHwnd == IntPtr.Zero) return;

        IntPtr textPtr = Marshal.StringToHGlobalUni(text);
        var ti = new TOOLINFOW
        {
            cbSize = Marshal.SizeOf<TOOLINFOW>(),
            uFlags = TtfIdIsHwnd | TtfSubclass,
            hwnd = _hwnd,
            uId = _hwnd,
            lpszText = textPtr,
        };
        IntPtr mem = Marshal.AllocHGlobal(ti.cbSize);
        try
        {
            Marshal.StructureToPtr(ti, mem, false);
            SendMessageW(_tooltipHwnd, TtmUpdateTipTextW, IntPtr.Zero, mem);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
            Marshal.FreeHGlobal(textPtr);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmMouseActivate:
                return new IntPtr(MaNoActivate);

            case WmTimer:
                if (!_testMode)
                {
                    long tid = wParam.ToInt64();
                    if (tid == TimerIdMain)
                        TickLoop();
                    else if (tid == TimerIdHover)
                        HandleHoverTimer();
                }
                return IntPtr.Zero;

            case WmFallbackReevaluate:
                EnsureModeAndPosition(forceRender: true);
                return IntPtr.Zero;

            case WmStateChanged:
                Interlocked.Exchange(ref _stateNotifyPending, 0);
                if (ReadStateIfChanged())
                {
                    _dirtyRender = true;
                    UpdateMainTimerCadence();
                    EnsureModeAndPosition(forceRender: false);
                }
                return IntPtr.Zero;

            case WmForegroundChanged:
                Interlocked.Exchange(ref _fgNotifyPending, 0);
                if (!_testMode)
                    EnsureModeAndPosition(forceRender: false);
                return IntPtr.Zero;

            case 0x02E0 /* WM_DPICHANGED */:
                _dirtyRender = true;
                EnsureModeAndPosition(forceRender: true);
                return IntPtr.Zero;

            case 0x001A /* WM_SETTINGCHANGE */:
            case 0x031A /* WM_THEMECHANGED */:
                _cachedLightTick = 0;
                s_win11CenteredTick = 0;
                _dirtyRender = true;
                return IntPtr.Zero;

            case WmLButtonDown:
                HandleMouseDown();
                return IntPtr.Zero;

            case WmMouseMove:
            {
                if (!_trackingLeave)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TmeLeave,
                        hwndTrack = hwnd,
                    };
                    TrackMouseEvent(ref tme);
                    _trackingLeave = true;
                }
                int lp = lParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                HandleMouseMove(x, y);
                return IntPtr.Zero;
            }

            case WmMouseLeave:
                _trackingLeave = false;
                HandleMouseLeave();
                return IntPtr.Zero;

            case WmLButtonUp:
            {
                int lp = lParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                HandleMouseUp(x, y);
                return IntPtr.Zero;
            }

            case WmDestroy:
                KillTimer(hwnd, TimerIdMain);
                KillTimer(hwnd, TimerIdHover);
                if (!_testMode)
                    PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Program.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void StartStateWatcher()
    {
        if (_stateWatcher != null || !IsHandleCreated) return;
        try
        {
            Directory.CreateDirectory(Program.BaseDir);
            _stateWatcher = new FileSystemWatcher(Program.BaseDir, "widget-state.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChanged = (_, _) =>
            {
                if (!IsHandleCreated) return;
                if (Interlocked.Exchange(ref _stateNotifyPending, 1) == 0)
                    PostMessageW(_hwnd, WmStateChanged, IntPtr.Zero, IntPtr.Zero);
            };
            RenamedEventHandler onRenamed = (_, _) =>
            {
                if (!IsHandleCreated) return;
                if (Interlocked.Exchange(ref _stateNotifyPending, 1) == 0)
                    PostMessageW(_hwnd, WmStateChanged, IntPtr.Zero, IntPtr.Zero);
            };
            _stateWatcher.Changed += onChanged;
            _stateWatcher.Created += onChanged;
            _stateWatcher.Renamed += onRenamed;
        }
        catch { }
    }

    private void UpdateMainTimerCadence()
    {
        if (!IsHandleCreated || _testMode) return;
        uint desired = _state.Monitor ? 1000u : 3000u;
        if (desired == _mainTimerMs) return;
        _mainTimerMs = desired;
        SetTimer(_hwnd, TimerIdMain, desired, IntPtr.Zero);
    }

    private void HandleHoverTimer()
    {
        KillTimer(Handle, TimerIdHover);
        if (_testMode || !_state.FlyoutOnHover || _dragging || _monitorHoverSent || _hover < 0 || _hover >= _blocks.Count)
            return;

        _monitorHoverSent = true;
        var hb = _blocks[_hover];
        var pt = new POINT { X = hb.Bounds.X, Y = hb.Bounds.Y };
        ClientToScreen(Handle, ref pt);
        string cmd = hb.Kind == BlockKind.Monitor
            ? "__monitor_hover__"
            : $"__group_hover__:{hb.GroupId}";
        WriteCommand(cmd, pt.X, pt.Y, hb.Bounds.Width, hb.Bounds.Height);
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
        if (!IsHandleCreated || _testMode || !_state.Embedded || _usingChildMode)
            return;
        if (IsPopupFlyoutOpen())
            return;
        if (Interlocked.Exchange(ref _fgNotifyPending, 1) == 0)
            PostMessageW(Handle, WmForegroundChanged, IntPtr.Zero, IntPtr.Zero);
    }

    private void TickLoop()
    {
        if (ReadStateIfChanged())
        {
            _dirtyRender = true;
            UpdateMainTimerCadence();
        }

        long now = Environment.TickCount64;
        if (_state.Monitor && (_lastStatsTick == 0 || now - _lastStatsTick >= 1000))
        {
            _metrics = SystemMetricsEngine.Sample(SystemMetricsEngine.FromMetricIds(_state.MonitorTop, _state.MonitorBottom));
            _lastStatsTick = now;
            _dirtyRender = true;
        }

        bool light = IsLightCached();
        if (light != _lastLight)
        {
            _lastLight = light;
            _dirtyRender = true;
        }

        EnsureModeAndPosition(forceRender: false);

        if ((!_initialTrimDone && now - _startTick >= 2500)
            || (_initialTrimDone && now - _lastTrimTick >= 300_000))
        {
            _initialTrimDone = true;
            _lastTrimTick = now;
            TrimWorkingSet();
        }
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
                    Program.SetDebugLogEnabled(parsed.Debug);
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
                    var wa = GetPrimaryWorkArea();
                    _floatX = wa.Right - _width - (int)Math.Round(24 * scale);
                    _floatY = wa.Bottom - _height - (int)Math.Round(24 * scale);
                }
            }
            var vs = GetVirtualScreen();
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

    private void ReleaseCachedDib()
    {
        if (_oldDibBmp != IntPtr.Zero && _dibMemDc != IntPtr.Zero)
            Program.SelectObject(_dibMemDc, _oldDibBmp);
        _oldDibBmp = IntPtr.Zero;
        if (_hDib != IntPtr.Zero)
        {
            Program.DeleteObject(_hDib);
            _hDib = IntPtr.Zero;
        }
        if (_dibMemDc != IntPtr.Zero)
        {
            Program.DeleteDC(_dibMemDc);
            _dibMemDc = IntPtr.Zero;
        }
        if (_dibScreenDc != IntPtr.Zero)
        {
            Program.ReleaseDC(IntPtr.Zero, _dibScreenDc);
            _dibScreenDc = IntPtr.Zero;
        }
        _dibBitsPtr = IntPtr.Zero;
        _dibW = 0;
        _dibH = 0;
    }

    private bool ApplyBitmapToWindow(SKBitmap bmp, bool allowFallback)
    {
        if (_dibScreenDc == IntPtr.Zero || _dibMemDc == IntPtr.Zero || _hDib == IntPtr.Zero
            || _dibBitsPtr == IntPtr.Zero || _dibW != bmp.Width || _dibH != bmp.Height)
        {
            ReleaseCachedDib();
            _dibScreenDc = Program.GetDC(IntPtr.Zero);
            _dibMemDc = Program.CreateCompatibleDC(_dibScreenDc);
            var bmi = new Program.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Program.BITMAPINFOHEADER>(),
                biWidth = bmp.Width,
                biHeight = -bmp.Height, // top-down matching SKBitmap memory layout
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            _hDib = Program.CreateDIBSection(_dibMemDc, ref bmi, 0, out _dibBitsPtr, IntPtr.Zero, 0);
            if (_hDib == IntPtr.Zero || _dibBitsPtr == IntPtr.Zero)
            {
                ReleaseCachedDib();
                return false;
            }
            _oldDibBmp = Program.SelectObject(_dibMemDc, _hDib);
            _dibW = bmp.Width;
            _dibH = bmp.Height;
        }

        int rowBytes = bmp.Width * 4;
        using var pixmap = bmp.PeekPixels();
        if (pixmap == null || !pixmap.ReadPixels(pixmap.Info, _dibBitsPtr, rowBytes, 0, 0))
            return false;

        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var ptSrc = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };

        bool ok = UpdateLayeredWindow(Handle, _dibScreenDc, IntPtr.Zero, ref size, _dibMemDc, ref ptSrc, 0, ref blend, UlwAlpha);
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
            ok = UpdateLayeredWindowWithPos(Handle, _dibScreenDc, ref ptDst, ref size, _dibMemDc, ref ptSrc, 0, ref blend, UlwAlpha);
        }

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Program.Log($"UpdateLayeredWindow failed err={err} child={_usingChildMode}");
            if (allowFallback && _usingChildMode && !_childLayeredFailed)
            {
                _childLayeredFailed = true;
                PostMessageW(Handle, WmFallbackReevaluate, IntPtr.Zero, IntPtr.Zero);
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

    private void HandleMouseDown()
    {
        KillTimer(Handle, TimerIdHover);
        _dragging = true;
        _moved = false;
        GetCursorPos(out _dragCursorStart);
        _dragWinStartX = _state.Embedded ? _relX : _floatX;
        _dragWinStartY = _state.Embedded ? _relY : _floatY;
        SetCapture(Handle);
    }

    private void HandleMouseMove(int x, int y)
    {
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
                    var vs = GetVirtualScreen();
                    _floatX = Math.Clamp(_dragWinStartX + dx, vs.Left, Math.Max(vs.Left, vs.Right - _width));
                    _floatY = Math.Clamp(_dragWinStartY + dy, vs.Top, Math.Max(vs.Top, vs.Bottom - _height));
                    SetWindowPos(Handle, HwndTopMost, _floatX, _floatY, 0, 0, SwpNoActivate | SwpNoSize);
                    _appliedX = _floatX; _appliedY = _floatY;
                }
                return;
            }
        }

        int hit = _blocks.FindIndex(b => b.Bounds.Contains(x, y));
        if (hit != _hover)
        {
            _hover = hit;
            _monitorHoverSince = Environment.TickCount64;
            _monitorHoverSent = false;
            if (_state.FlyoutOnHover && hit >= 0)
                SetTimer(Handle, TimerIdHover, 180, IntPtr.Zero);
            else
                KillTimer(Handle, TimerIdHover);

            if (_state.FlyoutOnHover || (hit >= 0 && _blocks[hit].Kind == BlockKind.Monitor))
                SetTipText("");
            else
                SetTipText(hit >= 0 ? _blocks[hit].Tip : "");
            RenderCurrent();
        }
    }

    private void HandleMouseLeave()
    {
        KillTimer(Handle, TimerIdHover);
        _monitorHoverSent = false;
        if (_hover != -1)
        {
            _hover = -1;
            SetTipText("");
            RenderCurrent();
        }
    }

    private void HandleMouseUp(int x, int y)
    {
        ReleaseCapture();
        _dragging = false;
        if (_moved)
        {
            _moved = false;
            SavePosition();
            return;
        }

        var hit = _blocks.FirstOrDefault(b => b.Bounds.Contains(x, y));
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

    private static RECT GetPrimaryWorkArea()
    {
        var rc = new RECT();
        if (SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, ref rc, 0) && rc.Right > rc.Left && rc.Bottom > rc.Top)
            return rc;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    private static RECT GetVirtualScreen()
    {
        int left = GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */);
        int top = GetSystemMetrics(77 /* SM_YVIRTUALSCREEN */);
        int w = GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */);
        int h = GetSystemMetrics(79 /* SM_CYVIRTUALSCREEN */);
        if (w <= 0 || h <= 0)
            return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        return new RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    private static bool IsWin11CenteredWithWidgets(IntPtr tray)
    {
        long now = Environment.TickCount64;
        if (s_win11CenteredTick != 0 && s_win11CenteredTray == tray && now - s_win11CenteredTick < 5000)
            return s_win11CenteredVal;

        bool result = false;
        try
        {
            if (FindWindowEx(tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
                bool centered = key?.GetValue("TaskbarAl") is not int al || al != 0;
                bool widgetsOn = key?.GetValue("TaskbarDa") is not int da || da != 0;
                result = centered && widgetsOn;
            }
        }
        catch { result = false; }

        s_win11CenteredTray = tray;
        s_win11CenteredVal = result;
        s_win11CenteredTick = now;
        return result;
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

    private bool IsLightCached()
    {
        long now = Environment.TickCount64;
        if (_cachedLightTick != 0 && now - _cachedLightTick < 5000)
            return _cachedLightVal;

        _cachedLightVal = IsLight();
        _cachedLightTick = now;
        return _cachedLightVal;
    }

    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stateWatcher != null)
        {
            try { _stateWatcher.EnableRaisingEvents = false; _stateWatcher.Dispose(); } catch { }
            _stateWatcher = null;
        }
        ReleaseCachedDib();
        if (_hookFg != IntPtr.Zero) { UnhookWinEvent(_hookFg); _hookFg = IntPtr.Zero; }
        _winEventProc = null;
        if (_tooltipHwnd != IntPtr.Zero) { Program.DestroyWindow(_tooltipHwnd); _tooltipHwnd = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero)
        {
            KillTimer(_hwnd, TimerIdMain);
            KillTimer(_hwnd, TimerIdHover);
            Program.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        Program.UnregisterClassW(_className, _hInstance);
        GC.KeepAlive(_wndProc);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

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
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Program.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOOLINFOW
    {
        public int cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public IntPtr uId;
        public RECT rect;
        public IntPtr hinst;
        public IntPtr lpszText;
        public IntPtr lParam;
        public IntPtr lpReserved;
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
