using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BorgStudio.Core.Notifications;

/// <summary>Sends a notification as a plain-text e-mail over SMTP (MailKit).</summary>
public sealed class EmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <exception cref="NotificationException" />
    public async Task SendAsync(EmailChannel channel, NotificationMessage message, string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel.Host))
            throw new NotificationException("The SMTP server is missing.");
        if (!MailboxAddress.TryParse(channel.From.Trim(), out var from))
            throw new NotificationException("The sender address is missing or invalid.");
        var recipients = channel.Recipients;
        if (recipients.Count == 0)
            throw new NotificationException("The recipient address is missing.");

        var mail = new MimeMessage();
        mail.From.Add(from);
        foreach (var recipient in recipients)
        {
            if (!MailboxAddress.TryParse(recipient, out var to))
                throw new NotificationException($"\"{recipient}\" is not an e-mail address.");
            mail.To.Add(to);
        }
        mail.Subject = $"[BorgStudio] {message.Title}";
        mail.Date = message.Time;
        mail.Body = new TextPart("plain") { Text = message.Body + $"\n\n-- \nBorgStudio · {message.Host}\n" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await smtp.ConnectAsync(channel.Host.Trim(), channel.Port, Options(channel), timeout.Token);
            if (channel.UserName.Trim() is { Length: > 0 } user)
                await smtp.AuthenticateAsync(user, password ?? "", timeout.Token);
            await smtp.SendAsync(mail, timeout.Token);
            await smtp.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationException($"{channel.Host} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (AuthenticationException exception)
        {
            throw new NotificationException($"Login failed: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException or SslHandshakeException or ProtocolException
                                              or CommandException or ServiceNotConnectedException or NotSupportedException)
        {
            throw new NotificationException(exception.Message, exception);
        }
    }

    internal static SecureSocketOptions Options(EmailChannel channel) => channel.Security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}
