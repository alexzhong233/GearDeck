using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace GearDeck.Monitor;

public sealed partial class MonitorWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ulong _lastIdle, _lastKernel, _lastUser;

    public MonitorWindow()
    {
        InitializeComponent();
        Title = "GearDeck Monitor";

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        var area = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(
            new RectInt32(area.X + area.Width - 260, area.Y + 12, 240, 48));

        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    private void Update()
    {
        double? cpu = ReadCpuPercent();
        CpuText.Text = cpu is { } c ? $"CPU {c:F0}%" : "CPU --";
        RamText.Text = $"RAM {RamUsedPercent():F0}%";
    }

    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            return null;
        double cpu = 0;
        if (_lastUser > 0)
        {
            ulong total = (kernel - _lastKernel) + (user - _lastUser);
            ulong busy = total - (idle - _lastIdle);
            if (total > 0)
                cpu = 100.0 * busy / total;
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        return cpu;
    }

    private static double RamUsedPercent()
    {
        var status = new NativeMemoryStatus
        {
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemoryStatus>(),
        };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return 0;
        return 100.0 * (status.TotalPhys - status.AvailPhys) / status.TotalPhys;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatus status);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
