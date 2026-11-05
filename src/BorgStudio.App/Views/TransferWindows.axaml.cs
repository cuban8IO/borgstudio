using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Nested dialogs (save file, server confirmation) belong to this window.
        if (DataContext is ExportViewModel export)
            export.Dialogs = new DialogService(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Don't leave a half-written export behind.
        if (DataContext is ExportViewModel { IsBusy: true })
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Nested dialogs (PIN, confirmation) belong to this window.
        if (DataContext is ImportViewModel import)
            import.Dialogs = new DialogService(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Don't stop an import halfway.
        if (DataContext is ImportViewModel { IsBusy: true })
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
