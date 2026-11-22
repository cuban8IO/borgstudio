using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace BorgStudio.App.Views;

/// <summary>A public key to add by hand somewhere else, with a copy button; the dialog result is <c>true</c> once it is added.</summary>
public partial class PublicKeyWindow : Window
{
    // Designer only.
    public PublicKeyWindow() : this("Title", "Instructions", "ssh-ed25519 AAAA… comment")
    {
    }

    public PublicKeyWindow(string title, string instructions, string publicKey)
    {
        InitializeComponent();
        Title = title;
        InstructionsText.Text = instructions;
        KeyText.Text = publicKey;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(KeyText.Text);
            CopiedText.IsVisible = true;
        }
    }

    private void Added_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
