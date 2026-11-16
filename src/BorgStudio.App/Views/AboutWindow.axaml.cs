using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Version, links (website, guide, source, legal notice, privacy) and the "no warranty" notice.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = BorgTexts.Format(Strings.AboutVersion, AppVersion.Current);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
