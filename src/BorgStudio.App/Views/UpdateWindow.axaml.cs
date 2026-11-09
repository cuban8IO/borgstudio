using Avalonia.Controls;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Update: newest release and what is new, then download with progress, then restart into the installer.</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (Update is { } update)
                await update.BeginAsync();
        };
    }

    private UpdateViewModel? Update => DataContext as UpdateViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Update is { } update)
        {
            update.Dialogs = new DialogService(this);
            update.CloseRequested += (_, _) => Close();
        }
    }

    // Closed while downloading: stop the download.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Update?.Abandon();
        base.OnClosing(e);
    }
}
