using BorgStudio.App.Resources;

namespace BorgStudio.App.ViewModels;

/// <summary>The "no warranty" notice: once, at the first start (also after updating from a version without it).</summary>
public partial class MainViewModel
{
    private bool _warrantyShowing;

    /// <summary>
    /// Shows the notice unless it was shown before. Waits for the app to be visible: with a PIN it comes after
    /// unlocking (the vault's state change calls this again).
    /// </summary>
    public async Task ShowWarrantyNoticeIfDueAsync()
    {
        if (Settings.WarrantyNoticeShown || IsLocked || _warrantyShowing)
            return;

        _warrantyShowing = true;
        try
        {
            await _services.Dialogs.ShowMessageAsync(Strings.WarrantyTitle, Strings.WarrantyMessage, Strings.WarrantyConfirm);
            UpdateSettings(Settings with { WarrantyNoticeShown = true });
        }
        finally
        {
            _warrantyShowing = false;
        }
    }
}
