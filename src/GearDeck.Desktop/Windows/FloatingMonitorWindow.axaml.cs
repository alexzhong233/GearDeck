using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GearDeck.Desktop.Services;

namespace GearDeck.Desktop.Windows;

/// <summary>
/// 可拖动、置顶、透明的桌面监控悬浮条；通过 ShowOrToggle()/CloseIfOpen() 静态管理开关。
/// </summary>
public sealed partial class FloatingMonitorWindow : Window
{
    private static FloatingMonitorWindow? _instance;
    private static int _lastX = int.MinValue;
    private static int _lastY = int.MinValue;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dragging;
    private PixelPoint _dragStartWindow;
    private Point _dragStartPointer;

    public static bool IsOpen => _instance != null;
    public static event Action? StateChanged;

    public FloatingMonitorWindow()
    {
        InitializeComponent();
        Title = "GearDeck Floating Monitor";

        var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int x = _lastX != int.MinValue ? _lastX : Math.Max(area.X, area.Right - 440);
        int y = _lastY != int.MinValue ? _lastY : area.Y + 12;
        Position = new PixelPoint(x, y);

        CloseBtn.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            _timer.Stop();
            StateChanged?.Invoke();
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>显示悬浮条；若已显示则关闭。</summary>
    public static void ShowOrToggle()
    {
        void Run()
        {
            if (_instance != null)
            {
                TryCloseCore();
                return;
            }
            var window = new FloatingMonitorWindow();
            _instance = window;
            window.Show();
            StateChanged?.Invoke();
        }
        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Invoke(Run);
    }

    /// <summary>关闭悬浮条（若已打开）。</summary>
    public static void CloseIfOpen()
    {
        if (Dispatcher.UIThread.CheckAccess()) TryCloseCore();
        else Dispatcher.UIThread.Invoke(TryCloseCore);
    }

    private static void TryCloseCore()
    {
        var window = _instance;
        _instance = null;
        if (window != null)
        {
            try { window.Close(); }
            catch { }
        }
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStartPointer = e.GetPosition(this);
        _dragStartWindow = Position;
        e.Pointer.Capture(this);
    }

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        Point current = e.GetPosition(this);
        Position = new PixelPoint(
            _dragStartWindow.X + (int)Math.Round(current.X - _dragStartPointer.X),
            _dragStartWindow.Y + (int)Math.Round(current.Y - _dragStartPointer.Y));
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _lastX = Position.X;
        _lastY = Position.Y;
        e.Pointer.Capture(null);
    }

    private void Refresh()
    {
        if (_instance != null && !ReferenceEquals(_instance, this)) return;
        var s = DesktopMetricsEngine.Sample();
        CpuText.Text = $"CPU {s.CpuPercent:F0}%";
        RamText.Text = $"RAM {s.RamPercent:F0}%";
        GpuText.Text = s.GpuPercent is { } gpu ? $"GPU {gpu:F0}%" : "GPU --";
        NetText.Text = $"↓{DesktopMetricsEngine.FormatCompactSpeed(s.NetDownBytesPerSec)} ↑{DesktopMetricsEngine.FormatCompactSpeed(s.NetUpBytesPerSec)}";
        DiskText.Text = $"DSK {s.DiskActivePercent:F0}%";
    }
}
