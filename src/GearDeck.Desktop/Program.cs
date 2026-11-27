using Avalonia;
using GearDeck.Core;

namespace GearDeck.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
