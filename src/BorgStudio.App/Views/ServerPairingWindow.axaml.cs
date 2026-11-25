using Avalonia.Controls;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Shows the pairing code and waits until the server confirms it; closes by itself once approved.</summary>
public partial class ServerPairingWindow : Window
{
    public ServerPairingWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is not ServerPairingViewModel pairing)
                return;
            pairing.CloseRequested += (_, _) => Close();
            // Closed with the window's own button: no more asking the server.
            Closed += (_, _) => pairing.Stop();
            await pairing.OpenBrowserCommand.ExecuteAsync(null);
            await pairing.RunAsync();
        };
    }
}
