using Avalonia.Controls;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Two archives compared; borg compares while the window is open.</summary>
public partial class ArchiveDiffWindow : Window
{
    public ArchiveDiffWindow() => InitializeComponent();

    private ArchiveDiffViewModel? Diff => DataContext as ArchiveDiffViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Diff is { } diff)
            diff.CloseRequested += (_, _) => Close();
    }

    // Closed while borg still compares: stop it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Diff?.Cancel?.Invoke();
        base.OnClosing(e);
    }
}
