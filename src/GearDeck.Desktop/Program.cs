using Avalonia;
using GearDeck.Core;

namespace GearDeck.Desktop;

internal static class Program
{
    internal static bool UseSoftwareRendering { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        bool forceGpu = args.Any(a => string.Equals(a, "--gpu", StringComparison.OrdinalIgnoreCase))
            || string.Equals(Environment.GetEnvironmentVariable("GEARDECK_GPU"), "1", StringComparison.Ordinal);
        UseSoftwareRendering = OperatingSystem.IsWindows() && !forceGpu;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
            Diag.LogCrash("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Diag.LogCrash("Program.Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (UseSoftwareRendering)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            });
        }
        return builder;
    }
}
