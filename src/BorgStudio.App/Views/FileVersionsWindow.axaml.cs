using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>The versions of a file across all archives; looking through the archives starts when it opens.</summary>
public partial class FileVersionsWindow : Window
{
    public FileVersionsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Versions?.Begin();
    }

    private FileVersionsViewModel? Versions => DataContext as FileVersionsViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Versions is { } versions)
            versions.Dialogs = new DialogService(this); // questions and the restore dialog belong to this window
    }

    // Closed while borg reads an archive: stop it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Versions?.Abandon();
        base.OnClosing(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>Rows of archives without the file are shown dimmed.</summary>
public sealed class VersionRowOpacity : IValueConverter
{
    public static readonly VersionRowOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 0.55 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
