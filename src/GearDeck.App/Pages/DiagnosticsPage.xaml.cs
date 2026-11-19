using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using GearDeck.Core;
using Windows.ApplicationModel.DataTransfer;

namespace GearDeck.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var lines = new List<string>();
        lines.AddRange(Diag.DumpHidInventory());
        lines.Add("");
        lines.AddRange(Diag.Snapshot());
        LogBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(LogBox.Text);
        Clipboard.SetContent(package);
    }
}
