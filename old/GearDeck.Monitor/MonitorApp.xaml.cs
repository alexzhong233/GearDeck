using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GearDeck.Monitor;

public partial class MonitorApp : Application
{
    private MonitorWindow? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MonitorWindow();
        _window.Activate();
    }
}
