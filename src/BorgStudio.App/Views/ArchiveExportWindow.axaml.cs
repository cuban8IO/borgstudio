using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>An archive exported into one file; it starts when the window opens, closing stops it.</summary>
public partial class ArchiveExportWindow : Window
{
    public ArchiveExportWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Export?.Begin();
    }

    private ArchiveExportViewModel? Export => DataContext as ArchiveExportViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Export is { } export)
            export.Dialogs = new DialogService(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Export?.Abandon();
        base.OnClosing(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
