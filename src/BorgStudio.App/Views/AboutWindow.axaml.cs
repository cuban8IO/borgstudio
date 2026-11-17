using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Version, links (website, guide, source, legal notice, privacy, licenses) and the "no warranty" notice.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = BorgTexts.Format(Strings.AboutVersion, AppVersion.Current);
        // Published packages carry the third-party licenses; a local build does not.
        ThirdPartyLink.NavigateUri = AppLinks.ThirdPartyNotices();
        ThirdPartyLink.IsVisible = ThirdPartyLink.NavigateUri is not null;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
