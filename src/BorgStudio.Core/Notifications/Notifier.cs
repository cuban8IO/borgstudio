using BorgStudio.Core.Processes;

namespace BorgStudio.Core.Notifications;

public enum NotificationChannelKind
{
    System,
    Email,
    Webhook,
}

/// <summary>How sending to one channel went; <see cref="Error"/> is <c>null</c> if it worked.</summary>
public sealed record ChannelResult(NotificationChannelKind Channel, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Hands a notification to every channel that reports its event – one failing channel does not stop the others.</summary>
public sealed class Notifier(SystemNotifier system, EmailSender email, WebhookSender webhook)
{
    /// <param name="executable">BorgStudio's executable (for the start menu shortcut Windows needs to show toasts).</param>
    public static Notifier CreateDefault(string? iconPath, string? executable) =>
        new(new SystemNotifier(new ProcessRunner(), iconPath, executable), new EmailSender(), new WebhookSender(SharedHttp));

    private static readonly HttpClient SharedHttp = new();

    /// <param name="secret">Reads a secret by name (see <see cref="NotificationSecrets"/>).</param>
    public async Task<IReadOnlyList<ChannelResult>> SendAsync(NotificationSettings settings, NotificationMessage message,
        Func<string, string?> secret, CancellationToken cancellationToken = default)
    {
        var sending = new List<Task<ChannelResult>>();
        if (settings.System.Reports(message.Event))
            sending.Add(TryAsync(NotificationChannelKind.System, () => system.SendAsync(message, cancellationToken)));
        if (settings.Email.Reports(message.Event))
            sending.Add(TryAsync(NotificationChannelKind.Email,
                () => email.SendAsync(settings.Email, message, secret(NotificationSecrets.SmtpPassword), cancellationToken)));
        if (settings.Webhook.Reports(message.Event))
            sending.Add(TryAsync(NotificationChannelKind.Webhook,
                () => webhook.SendAsync(settings.Webhook, message, secret(NotificationSecrets.WebhookHeader), cancellationToken)));
        return await Task.WhenAll(sending);
    }

    /// <summary>Sends <paramref name="message"/> through one channel as it is set up now (enabled or not): the "Test" button.</summary>
    /// <param name="secretValue">The channel's password or token as typed (it may not be saved yet).</param>
    public Task<ChannelResult> TestAsync(NotificationChannel channel, NotificationMessage message, string? secretValue,
        CancellationToken cancellationToken = default) => channel switch
    {
        EmailChannel mail => TryAsync(NotificationChannelKind.Email, () => email.SendAsync(mail, message, secretValue, cancellationToken)),
        WebhookChannel hook => TryAsync(NotificationChannelKind.Webhook, () => webhook.SendAsync(hook, message, secretValue, cancellationToken)),
        _ => TryAsync(NotificationChannelKind.System, () => system.SendAsync(message, cancellationToken)),
    };

    private static async Task<ChannelResult> TryAsync(NotificationChannelKind channel, Func<Task> send)
    {
        try
        {
            await send();
            return new ChannelResult(channel, null);
        }
        catch (NotificationException exception)
        {
            return new ChannelResult(channel, exception.Message);
        }
        catch (TimeoutException)
        {
            return new ChannelResult(channel, "No answer in time.");
        }
    }
}
