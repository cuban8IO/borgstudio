using BorgStudio.Core.Backups;

namespace BorgStudio.Core.Notifications;

/// <summary>One notification, worded for people (<see cref="Title"/>, <see cref="Body"/>) and with the details for machines.</summary>
public sealed record NotificationMessage(NotificationEvent Event, string Title, string Body)
{
    public string? Job { get; init; }

    public Guid? JobId { get; init; }

    public string? Repository { get; init; }

    /// <summary>The computer that ran the backup.</summary>
    public string Host { get; init; } = Environment.MachineName;

    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    /// <summary>The run it is about; <c>null</c> for a test message.</summary>
    public BackupRunSummary? Run { get; init; }

    /// <summary>The storage it is about (storage events).</summary>
    public Storage.StorageUsage? Storage { get; init; }

    /// <summary>The check it is about (check events).</summary>
    public Checks.CheckRecord? Check { get; init; }

    /// <summary>The restore test it is about.</summary>
    public Checks.RestoreTestRecord? RestoreTest { get; init; }

    /// <summary>Sent with the "Test" button.</summary>
    public bool IsTest { get; init; }

    /// <summary>"Title" and the body, for receivers with one text only.</summary>
    public string FullText => $"{Title}\n\n{Body}";
}

/// <summary>A notification could not be sent; the message says why (shown to the user).</summary>
public sealed class NotificationException(string message, Exception? inner = null) : Exception(message, inner);
