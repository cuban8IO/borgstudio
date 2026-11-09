using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;

namespace BorgStudio.App.ViewModels;

/// <summary>Copies a private key file into the vault; asks for the key's password if it has one.</summary>
public static class SshKeyImport
{
    /// <summary>The imported key, or <c>null</c> with the reason (none if the user cancelled).</summary>
    public static async Task<(SshKeyEntry? Key, string? Error)> ImportAsync(AppServices services, IDialogService dialogs, string file)
    {
        string? password = null;
        while (true)
        {
            var read = await services.SshKeygen.ReadAsync(file, password, services.SshKeys.Directory);
            switch (read.Status)
            {
                case SshKeyReadStatus.Success:
                    var duplicate = services.Vault.Keys.FirstOrDefault(key =>
                        SshPublicKey.WithoutComment(key.PublicKey) == SshPublicKey.WithoutComment(read.PublicKey!));
                    if (duplicate is not null)
                        return (duplicate, BorgTexts.Format(Strings.KeysAlreadyImported, duplicate.Name));

                    var entry = new SshKeyEntry
                    {
                        Id = Guid.NewGuid(),
                        Name = Path.GetFileName(file),
                        PrivateKey = read.PrivateKey!,
                        PublicKey = read.PublicKey!,
                        Created = DateTimeOffset.Now,
                    };
                    try
                    {
                        services.Vault.SetKey(entry);
                    }
                    catch (VaultLockedException)
                    {
                        return (null, Strings.VaultNotUnlocked);
                    }
                    return (entry, null);

                case SshKeyReadStatus.PasswordNeeded or SshKeyReadStatus.WrongPassword:
                    var prompt = BorgTexts.Format(Strings.KeysPasswordPrompt, Path.GetFileName(file));
                    var error = read.Status == SshKeyReadStatus.WrongPassword ? Strings.KeysPasswordWrong : null;
                    password = await dialogs.AskSecretAsync(Strings.KeysPasswordTitle, prompt, error);
                    if (password is null)
                        return (null, null);
                    break;

                default:
                    return (null, BorgTexts.Format(Strings.KeysImportFailed, read.Message ?? ""));
            }
        }
    }
}
