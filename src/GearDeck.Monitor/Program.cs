using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GearDeck.Monitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new MonitorApp();
        });
        return 0;
    }
}
